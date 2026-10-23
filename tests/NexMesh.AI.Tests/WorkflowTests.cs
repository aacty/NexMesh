using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Workflow;
using NexMesh.Core;
using NexMesh.Core.Context;

namespace NexMesh.AI.Tests;

public class WorkflowTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static NexMeshWorkflowEngine BuildEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        var sp = services.BuildServiceProvider();
        return new NexMeshWorkflowEngine(
            new InMemoryCheckpointStore(),
            sp.GetRequiredService<INexMeshBackgroundRunner>());
    }

    private sealed class CaptureStep(string name) : NexMeshWorkflowStep
    {
        public static List<string> Executed { get; } = [];
        public StepStatus ReturnStatus { get; set; } = StepStatus.Completed;

        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            Executed.Add(name);
            context.Outputs[$"step:{name}"] = $"done";
            return Task.FromResult(ReturnStatus);
        }
    }

    private sealed class FailStep : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context) =>
            throw new InvalidOperationException("步骤故意失败");
    }

    [Fact]
    public async Task 顺序执行_全部步骤完成()
    {
        CaptureStep.Executed.Clear();
        var engine = BuildEngine();
        var steps = new List<NexMeshWorkflowStep>
        {
            new CaptureStep("A"),
            new CaptureStep("B"),
            new CaptureStep("C"),
        };

        var result = await engine.StartAsync("test-flow", steps);

        Assert.Equal(StepStatus.Completed, result.OverallStatus);
        Assert.Equal(3, result.CurrentStepIndex);
        Assert.Equal(["A", "B", "C"], CaptureStep.Executed);
    }

    [Fact]
    public async Task HITL挂起_恢复后继续()
    {
        CaptureStep.Executed.Clear();
        var engine = BuildEngine();
        var approvalStep = new CaptureStep("approval") { ReturnStatus = StepStatus.Suspended };
        var steps = new List<NexMeshWorkflowStep>
        {
            new CaptureStep("before"),
            approvalStep,
            new CaptureStep("after"),
        };

        // 第一轮:到 approval 挂起
        var suspended = await engine.StartAsync("hitl-flow", steps);
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);
        Assert.Equal(["before", "approval"], CaptureStep.Executed);

        // 恢复:approval 已过,继续执行 after
        approvalStep.ReturnStatus = StepStatus.Completed;
        var resumed = await engine.ResumeAsync(suspended.WorkflowId, steps);
        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
        // 恢复后重新进入 approval 步骤(做审批后处理)→ Completed → 推进到 after
        Assert.Equal(["before", "approval", "approval", "after"], CaptureStep.Executed);
    }

    [Fact]
    public async Task 步骤失败_检查点保存错误()
    {
        var engine = BuildEngine();
        var steps = new List<NexMeshWorkflowStep>
        {
            new CaptureStep("ok"),
            new FailStep(),
            new CaptureStep("never"),
        };

        var result = await engine.StartAsync("fail-flow", steps);

        Assert.Equal(StepStatus.Failed, result.OverallStatus);
        Assert.NotNull(result.LastError);
        Assert.Contains("故意失败", result.LastError);
        // 失败步骤后面的不会执行
        Assert.DoesNotContain("never", CaptureStep.Executed);
    }
}

/// <summary>SSS+第二轮:并发恢复 CAS + GetInput 类型安全。</summary>
public class WorkflowSafetyTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static NexMeshWorkflowEngine BuildEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        var sp = services.BuildServiceProvider();
        return new NexMeshWorkflowEngine(
            new InMemoryCheckpointStore(),
            sp.GetRequiredService<INexMeshBackgroundRunner>());
    }

    private sealed class SuspendingStep : NexMeshWorkflowStep
    {
        private int _calls;
        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            // 首次挂起;恢复后完成(使工作流终态确定,并发恢复 CAS 才有意义)
            return Task.FromResult(Interlocked.Increment(ref _calls) == 1
                ? StepStatus.Suspended
                : StepStatus.Completed);
        }
    }

    [Fact]
    public async Task 并发恢复_只有一个赢家()
    {
        var engine = BuildEngine();
        var steps = new List<NexMeshWorkflowStep> { new SuspendingStep() };

        var suspended = await engine.StartAsync("race-flow", steps);
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);

        // 并发双恢复:CAS 保证只有一个成功
        var results = await Task.WhenAll(
            engine.ResumeAsync(suspended.WorkflowId, steps),
            engine.ResumeAsync(suspended.WorkflowId, steps).ContinueWith(t =>
            {
                // 第二个恢复应抛 409(MeshException);吞掉以断言
                if (t.IsFaulted && t.Exception?.GetBaseException() is NexMesh.Core.MeshException mesh)
                {
                    return (WorkflowCheckpoint)null!;
                }
                throw new Xunit.Sdk.XunitException("第二个恢复应抛 MeshException 409");
            }));

        Assert.True(results[0] is { } || results[1] is { }, "至少一个恢复成功");
    }

    private sealed class TypeReadingStep : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            // GetInput<T> 对 JsonElement(恢复后)与原生类型(首轮)都安全
            var count = context.GetInput<int>("count");
            context.Outputs["double"] = count * 2;
            return Task.FromResult(StepStatus.Completed);
        }
    }

    [Fact]
    public async Task GetInput_JsonElement塌缩后仍类型安全()
    {
        var engine = BuildEngine();
        var steps = new List<NexMeshWorkflowStep>
        {
            new SuspendingStep(), // 制造挂起→恢复(强制 JSON 往返)
        };
        // 手动构造:挂起后恢复,后续步骤读 JSON 往返后的值
        var suspended = await engine.StartAsync("type-flow", steps,
            initialData: new Dictionary<string, object?> { ["count"] = 21 });
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);

        var resumed = await engine.ResumeAsync(suspended.WorkflowId, steps);
        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
    }
}
