using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Workflow;
using NexMesh.Core;
using NexMesh.Core.Context;

namespace NexMesh.AI.Tests;

/// <summary>
/// J(第三轮交叉·工作流检查点语义):固化"设计保证"面的检查点/恢复语义图——
/// 进程重启断点续跑、IsResumed 一次性、挂起部分输出持久化、签名守卫、
/// RecoverStale 状态矩阵、崩溃窗口零重跑、终态固化。
/// </summary>
public class JWorkflowCheckpointSemanticsTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static NexMeshWorkflowEngine BuildEngine(IWorkflowCheckpointStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        var sp = services.BuildServiceProvider();
        return new NexMeshWorkflowEngine(
            store ?? new InMemoryCheckpointStore(),
            sp.GetRequiredService<INexMeshBackgroundRunner>());
    }

    /// <summary>跨"进程"共享调用计数与 IsResumed 观测的标记步骤(计数 key = 基类 Name)。</summary>
    private sealed class MarkerStep(
        ConcurrentDictionary<string, int> calls,
        List<bool> resumedFlags,
        Func<StepContext, StepStatus>? behavior = null) : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            calls.AddOrUpdate(Name, 1, (_, c) => c + 1);
            resumedFlags.Add(context.IsResumed);
            return Task.FromResult(behavior?.Invoke(context) ?? StepStatus.Completed);
        }
    }

    /// <summary>工厂:经对象初始化器设置基类 init-only Name(签名计算与计数都依赖它)。</summary>
    private static MarkerStep Step(
        string name,
        ConcurrentDictionary<string, int> calls,
        List<bool> flags,
        Func<StepContext, StepStatus>? behavior = null)
        => new(calls, flags, behavior) { Name = name };

    private sealed class ThrowingStep : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context) =>
            throw new InvalidOperationException("步骤故意失败");
    }

    [Fact]
    public async Task 进程重启_同存储新引擎新步骤实例_从断点续跑()
    {
        var calls = new ConcurrentDictionary<string, int>();
        var resumedFlags = new List<bool>();
        var store = new InMemoryCheckpointStore();

        var engine1 = BuildEngine(store);
        var stepsV1 = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, resumedFlags),
            Step("s2", calls, resumedFlags, ctx =>
            {
                if (!ctx.IsResumed)
                {
                    ctx.Outputs["ticket"] = "T1"; // 挂起前部分输出
                    return StepStatus.Suspended;
                }
                return StepStatus.Completed;
            }),
            Step("s3", calls, resumedFlags),
        };
        var suspended = await engine1.StartAsync("restart-flow", stepsV1);
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);
        Assert.Equal(1, suspended.CurrentStepIndex);

        // 模拟进程重启:同一持久化存储 + 全新引擎(重建 Provider/runner)+ 全新步骤实例
        var engine2 = BuildEngine(store);
        var stepsV2 = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, resumedFlags),
            Step("s2", calls, resumedFlags, ctx =>
            {
                // 恢复轮:验证挂起前写出的输出经 JSON 往返后仍可见
                Assert.Equal("T1", ctx.GetInput<string>("ticket"));
                return StepStatus.Completed;
            }),
            Step("s3", calls, resumedFlags),
        };
        var resumed = await engine2.ResumeAsync(suspended.WorkflowId, stepsV2);

        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
        Assert.Equal(3, resumed.CurrentStepIndex);
        Assert.Equal(1, calls["s1"]);
        Assert.Equal(2, calls["s2"]); // 首轮挂起 1 次 + 恢复轮恰好 1 次
        Assert.Equal(1, calls["s3"]);
        // IsResumed 仅标记恢复轮的第一个(原挂起)步骤
        Assert.Equal(new[] { false, false, true, false }, resumedFlags);
    }

    [Fact]
    public async Task 失败终态_Resume拒绝40904_RecoverStale拒绝()
    {
        // 设计现状固化:Failed 是终态,框架不提供失败重试(需业务自行 StartAsync 新实例)
        var store = new InMemoryCheckpointStore();
        var engine = BuildEngine(store);
        var calls = new ConcurrentDictionary<string, int>();
        var flags = new List<bool>();
        var steps = new List<NexMeshWorkflowStep>
        {
            Step("ok", calls, flags),
            new ThrowingStep(),
            Step("never", calls, flags),
        };
        var result = await engine.StartAsync("fail-flow", steps);
        Assert.Equal(StepStatus.Failed, result.OverallStatus);
        Assert.Contains("步骤故意失败", result.LastError);

        var resumeEx = await Assert.ThrowsAsync<MeshException>(
            () => engine.ResumeAsync(result.WorkflowId, steps));
        Assert.Equal(40904, resumeEx.ErrorCode);
        Assert.False(await engine.RecoverStaleAsync(result.WorkflowId));
        Assert.Equal(0, calls.GetValueOrDefault("never")); // 失败步骤之后不执行
    }

    [Fact]
    public async Task RecoverStale_状态矩阵_仅Running可转换且不回退数据()
    {
        var store = new InMemoryCheckpointStore();
        var engine = BuildEngine(store);
        var steps = new List<NexMeshWorkflowStep> { Step("m", [], []) };
        var signature = WorkflowCheckpoint.ComputeStepsSignature(steps);

        Assert.False(await engine.RecoverStaleAsync("no-such-id"));

        foreach (var status in new[] { StepStatus.Suspended, StepStatus.Completed, StepStatus.Failed })
        {
            var cp = new WorkflowCheckpoint
            {
                WorkflowName = "matrix",
                CurrentStepIndex = 1,
                OverallStatus = status,
                StepsSignature = signature,
                DataJson = """{"k":"v"}""",
            };
            await store.SaveAsync(cp);
            Assert.False(await engine.RecoverStaleAsync(cp.WorkflowId));
        }

        // 真挂死:Running + 携带进度 → 转 Suspended,但 index/DataJson 一字不动
        var stale = new WorkflowCheckpoint
        {
            WorkflowName = "matrix",
            CurrentStepIndex = 1,
            OverallStatus = StepStatus.Running,
            StepsSignature = signature,
            DataJson = """{"k":"v"}""",
        };
        await store.SaveAsync(stale);
        Assert.True(await engine.RecoverStaleAsync(stale.WorkflowId));
        var latest = await store.GetAsync(stale.WorkflowId);
        Assert.Equal(StepStatus.Suspended, latest!.OverallStatus);
        Assert.Equal(1, latest.CurrentStepIndex);
        Assert.Equal("""{"k":"v"}""", latest.DataJson);
    }

    [Fact]
    public async Task 崩溃于最后一步之后_恢复零重跑直达Completed()
    {
        // 崩溃窗口:最后一步已推进(index == steps.Count)但 Completed 状态尚未落盘
        var store = new InMemoryCheckpointStore();
        var engine = BuildEngine(store);
        var calls = new ConcurrentDictionary<string, int>();
        var steps = new List<NexMeshWorkflowStep>
        {
            Step("a", calls, []),
            Step("b", calls, []),
        };
        var crashed = new WorkflowCheckpoint
        {
            WorkflowName = "crash-window",
            CurrentStepIndex = 2,
            OverallStatus = StepStatus.Running,
            StepsSignature = WorkflowCheckpoint.ComputeStepsSignature(steps),
            DataJson = "{}",
        };
        await store.SaveAsync(crashed);

        Assert.True(await engine.RecoverStaleAsync(crashed.WorkflowId));
        var resumed = await engine.ResumeAsync(crashed.WorkflowId, steps);
        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
        Assert.Equal(2, resumed.CurrentStepIndex);
        Assert.Equal(0, calls.Values.Sum()); // 不重跑任何步骤
    }

    [Fact]
    public async Task 签名守卫_步骤序列变更拒绝恢复_同序新实例放行()
    {
        var engine = BuildEngine();
        var calls = new ConcurrentDictionary<string, int>();
        var flags = new List<bool>();
        var steps = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, flags),
            Step("s2", calls, flags, _ => StepStatus.Suspended),
            Step("s3", calls, flags),
        };
        var suspended = await engine.StartAsync("sig-flow", steps);
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);

        // 减一步
        var fewer = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, flags),
            Step("s2", calls, flags),
        };
        var exFewer = await Assert.ThrowsAsync<MeshException>(() => engine.ResumeAsync(suspended.WorkflowId, fewer));
        Assert.Equal(40906, exFewer.ErrorCode);

        // 加一步
        var more = steps.Append(Step("s4", calls, flags)).ToList();
        var exMore = await Assert.ThrowsAsync<MeshException>(() => engine.ResumeAsync(suspended.WorkflowId, more));
        Assert.Equal(40906, exMore.ErrorCode);

        // 改名
        var renamed = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, flags),
            Step("s2x", calls, flags),
            Step("s3", calls, flags),
        };
        var exRenamed = await Assert.ThrowsAsync<MeshException>(() => engine.ResumeAsync(suspended.WorkflowId, renamed));
        Assert.Equal(40906, exRenamed.ErrorCode);

        // 空步骤列表
        var exEmpty = await Assert.ThrowsAsync<MeshException>(
            () => engine.ResumeAsync(suspended.WorkflowId, []));
        Assert.Equal(40906, exEmpty.ErrorCode);

        // 同序同名的全新实例(进程重启形态)→ 放行
        var sameNames = new List<NexMeshWorkflowStep>
        {
            Step("s1", calls, flags),
            Step("s2", calls, flags),
            Step("s3", calls, flags),
        };
        var resumed = await engine.ResumeAsync(suspended.WorkflowId, sameNames);
        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
    }

    [Fact]
    public async Task StartAsync_空步骤列表_40002拒绝()
    {
        var engine = BuildEngine();
        var ex = await Assert.ThrowsAsync<MeshException>(
            () => engine.StartAsync("empty-flow", []));
        Assert.Equal(40002, ex.ErrorCode);
    }

    [Fact]
    public async Task 检查点DataJson损坏_恢复抛MeshException而非裸JsonException()
    {
        var store = new InMemoryCheckpointStore();
        var engine = BuildEngine(store);
        var calls = new ConcurrentDictionary<string, int>();
        var steps = new List<NexMeshWorkflowStep> { Step("a", calls, []) };
        var broken = new WorkflowCheckpoint
        {
            WorkflowName = "corrupt-flow",
            CurrentStepIndex = 0,
            OverallStatus = StepStatus.Suspended,
            StepsSignature = WorkflowCheckpoint.ComputeStepsSignature(steps),
            DataJson = "{broken-json",
        };
        await store.SaveAsync(broken);

        var ex = await Assert.ThrowsAsync<MeshException>(() => engine.ResumeAsync(broken.WorkflowId, steps));
        Assert.Equal(50005, ex.ErrorCode);
        Assert.Contains("检查点数据损坏", ex.Message);
    }
}
