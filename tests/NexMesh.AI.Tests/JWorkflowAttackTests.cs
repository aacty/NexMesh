using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Workflow;
using NexMesh.Core;
using NexMesh.Core.Context;

namespace NexMesh.AI.Tests;

/// <summary>
/// J(第三轮交叉·工作流攻击面):钉住第三轮修复后的语义——
/// ① 状态机门禁:步骤返回非法定结局一律拒绝推进(修复前会被静默当成功推进到 Completed);
/// ② RecoverStaleAsync 丢失更新:CAS 条件转换,预读后并发推进不回退检查点
///    (修复前整行写回旧快照,可把已完成流程回退成 Suspended 旧 index → 双执行);
/// ③ 取消 ≠ 失败:OCE 落盘为可恢复的 Suspended(修复前标 Failed 终态,流程随停机死亡);
/// ④ 并发恢复强断言:8 路并发恰好一个赢家、步骤恰好执行两次。
/// </summary>
public class JWorkflowAttackTests
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

    private sealed class FixedStatusStep(
        StepStatus status,
        ConcurrentDictionary<string, int>? calls = null) : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            calls?.AddOrUpdate("bad", 1, (_, c) => c + 1);
            return Task.FromResult(status);
        }
    }

    private sealed class CountingStep(
        string name,
        ConcurrentDictionary<string, int> calls,
        Func<StepContext, StepStatus> behavior) : NexMeshWorkflowStep
    {
        public override Task<StepStatus> ExecuteAsync(StepContext context)
        {
            calls.AddOrUpdate(name, 1, (_, c) => c + 1);
            return Task.FromResult(behavior(context));
        }
    }

    [Theory]
    [InlineData((int)StepStatus.Pending)]
    [InlineData((int)StepStatus.Running)]
    [InlineData((int)StepStatus.Failed)]
    [InlineData(99)] // 未定义枚举值
    public async Task 步骤返回非法状态_拒绝推进_工作流失败(int rawStatus)
    {
        var engine = BuildEngine();
        var calls = new ConcurrentDictionary<string, int>();
        var steps = new List<NexMeshWorkflowStep>
        {
            new FixedStatusStep((StepStatus)rawStatus, calls),
            new CountingStep("never", calls, _ => StepStatus.Completed),
        };

        var result = await engine.StartAsync("bad-return-flow", steps);

        Assert.Equal(StepStatus.Failed, result.OverallStatus);
        Assert.Contains("非法状态", result.LastError);
        Assert.Equal(0, result.CurrentStepIndex);          // 不推进
        Assert.Equal(1, calls["bad"]);
        Assert.Equal(0, calls.GetValueOrDefault("never")); // 后续步骤不执行
    }

    [Fact]
    public async Task 步骤返回Skipped_合法_正常推进()
    {
        var engine = BuildEngine();
        var calls = new ConcurrentDictionary<string, int>();
        var steps = new List<NexMeshWorkflowStep>
        {
            new FixedStatusStep(StepStatus.Skipped),
            new CountingStep("after", calls, _ => StepStatus.Completed),
        };

        var result = await engine.StartAsync("skip-flow", steps);

        Assert.Equal(StepStatus.Completed, result.OverallStatus);
        Assert.Equal(1, calls["after"]);
    }

    [Fact]
    public async Task 步骤取消_OCE落盘为挂起_可恢复续跑()
    {
        // 修复前:OCE 落进通用 catch → Failed 终态,40904 拒绝恢复,长流程随一次宿主停机全灭
        var store = new InMemoryCheckpointStore();
        var engine = BuildEngine(store);
        var calls = new ConcurrentDictionary<string, int>();
        var cts = new CancellationTokenSource();
        var steps = new List<NexMeshWorkflowStep>
        {
            new CountingStep("c1", calls, _ => StepStatus.Completed),
            new CountingStep("c2", calls, ctx =>
            {
                if (ctx.IsResumed)
                {
                    return StepStatus.Completed; // 恢复轮直接完成
                }
                cts.Cancel();
                ctx.CancellationToken.ThrowIfCancellationRequested();
                return StepStatus.Completed; // 不可达
            }),
            new CountingStep("c3", calls, _ => StepStatus.Completed),
        };

        var result = await engine.StartAsync("cancel-flow", steps, ct: cts.Token);

        Assert.Equal(StepStatus.Suspended, result.OverallStatus);
        Assert.Contains("取消", result.LastError);
        Assert.Equal(1, result.CurrentStepIndex);

        var resumed = await engine.ResumeAsync(result.WorkflowId, steps);
        Assert.Equal(StepStatus.Completed, resumed.OverallStatus);
        Assert.Equal(1, calls["c1"]);
        Assert.Equal(2, calls["c2"]); // 挂起 1 次 + 恢复轮 1 次
        Assert.Equal(1, calls["c3"]);
    }

    [Fact]
    public async Task 并发八路恢复_恰好一个赢家_步骤恰好执行两次()
    {
        var engine = BuildEngine();
        var calls = new ConcurrentDictionary<string, int>();
        var steps = new List<NexMeshWorkflowStep>
        {
            new CountingStep("gate", calls, _ =>
                calls.GetValueOrDefault("gate") == 1 ? StepStatus.Suspended : StepStatus.Completed),
        };
        var suspended = await engine.StartAsync("race8-flow", steps);
        Assert.Equal(StepStatus.Suspended, suspended.OverallStatus);

        static async Task<object?> SafeResume(
            NexMeshWorkflowEngine engine, string id, List<NexMeshWorkflowStep> steps)
        {
            try
            {
                return await engine.ResumeAsync(id, steps);
            }
            catch (MeshException ex)
            {
                return ex;
            }
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => SafeResume(engine, suspended.WorkflowId, steps)));

        var winners = outcomes.OfType<WorkflowCheckpoint>().ToList();
        var losers = outcomes.OfType<MeshException>().ToList();
        Assert.Single(winners);          // CAS:恰好一个赢家
        Assert.Equal(7, losers.Count);   // 其余全部 409
        Assert.All(losers, l => Assert.True(
            l.ErrorCode is 40904 or 40905,
            $"意外的错误码 {l.ErrorCode}:{l.Message}"));
        Assert.Equal(StepStatus.Completed, winners[0].OverallStatus);
        Assert.Equal(2, calls.GetValueOrDefault("gate")); // 挂起 1 次 + 恢复恰好 1 次,无双执行
    }

    /// <summary>包装存储:首次 GetAsync(RecoverStale 的预读)返回前注入"并发推进已完成"。</summary>
    private sealed class RacyRecoverStore : IWorkflowCheckpointStore
    {
        public readonly InMemoryCheckpointStore Inner = new();
        public Func<WorkflowCheckpoint, Task>? OnFirstGet;

        public Task SaveAsync(WorkflowCheckpoint checkpoint) => Inner.SaveAsync(checkpoint);
        public Task<List<WorkflowCheckpoint>> ListSuspendedAsync() => Inner.ListSuspendedAsync();
        public Task<bool> TryMarkRunningAsync(string workflowId, DateTime resumedAt) =>
            Inner.TryMarkRunningAsync(workflowId, resumedAt);
        public Task<bool> TryMarkSuspendedAsync(string workflowId) =>
            Inner.TryMarkSuspendedAsync(workflowId);

        public async Task<WorkflowCheckpoint?> GetAsync(string workflowId)
        {
            var checkpoint = await Inner.GetAsync(workflowId);
            if (checkpoint is not null && OnFirstGet is not null)
            {
                var hook = OnFirstGet;
                OnFirstGet = null;
                await hook(checkpoint); // 预读与落盘之间,世界继续前进
            }
            return checkpoint;
        }
    }

    [Fact]
    public async Task RecoverStale_预读后被并发推进完成_不回退检查点()
    {
        // 修复前:预读旧 Running 快照 → SaveAsync 整行写回(旧 index + Suspended),
        //         会把已完成的流程回退成可恢复态 → Resume 后步骤重复执行(丢失更新)。
        var store = new RacyRecoverStore();
        var steps = new List<NexMeshWorkflowStep>
        {
            new CountingStep("a", new ConcurrentDictionary<string, int>(), _ => StepStatus.Completed),
            new CountingStep("b", new ConcurrentDictionary<string, int>(), _ => StepStatus.Completed),
        };
        var crashed = new WorkflowCheckpoint
        {
            WorkflowName = "lost-update",
            CurrentStepIndex = 0,
            OverallStatus = StepStatus.Running,
            StepsSignature = WorkflowCheckpoint.ComputeStepsSignature(steps),
            DataJson = "{}",
        };
        await store.SaveAsync(crashed);

        store.OnFirstGet = async stale =>
        {
            // 模拟:预读与落盘之间,另一消费者恢复执行并把整个流程跑完
            await store.Inner.SaveAsync(stale with
            {
                OverallStatus = StepStatus.Completed,
                CurrentStepIndex = 2,
            });
        };

        var engine = BuildEngine(store);
        // 修复后:CAS 条件转换看到已 Completed → 拒绝,返回 false
        Assert.False(await engine.RecoverStaleAsync(crashed.WorkflowId));

        var latest = await store.GetAsync(crashed.WorkflowId);
        Assert.Equal(StepStatus.Completed, latest!.OverallStatus); // 不被回退
        Assert.Equal(2, latest.CurrentStepIndex);                  // 进度不丢
    }

    [Fact]
    public async Task GetInput_显式null与缺失键_返回默认值而非裸异常()
    {
        var engine = BuildEngine();
        string? note = "sentinel";
        var missingInt = -1;
        var steps = new List<NexMeshWorkflowStep>
        {
            new CountingStep("probe", new ConcurrentDictionary<string, int>(), ctx =>
            {
                note = ctx.GetInput<string?>("note");
                missingInt = ctx.GetInput<int>("missing");
                return StepStatus.Completed;
            }),
        };

        await engine.StartAsync("null-input-flow", steps,
            initialData: new Dictionary<string, object?> { ["note"] = null });

        Assert.Null(note);
        Assert.Equal(0, missingInt); // 修复前:对值类型 Deserialize("null") 抛裸 JsonException
    }
}
