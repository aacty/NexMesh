namespace NexMesh.AI.Approval.Tests;

using System.Collections.Concurrent;
using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.AI.Approval;
using NexMesh.AI.Tools;
using NexMesh.AiFixtures.High;
using NexMesh.Core;
using NexMesh.Core.Context;

/// <summary>
/// 攻击面 A(框架架构师 A 对抗性交叉测试):审批幂等键 TOCTOU 竞态。
/// ApprovalGate.EnqueueAsync(src/NexMesh.AI.Approval/ApprovalGate.cs:22-37)为
/// "FindPendingByIdempotencyKeyAsync 查重 → AddAsync 入队"两步非原子序列;
/// 两个 store 均无 IdempotencyKey 唯一约束(InMemory 按随机 Id 主键存;FreeSql 实体
/// ApprovalStores.cs:103 IdempotencyKey 仅普通列)。并发同参高危调用 → 多张待审单 →
/// 每张都可被合法批准 → 高危工具多次执行。契约原文(ApprovalModels.cs:36):
/// "模型重复请求同一高危操作不产生重复审批单"。
/// 判别器用 FreeSql 真库(真实异步 I/O 交出执行权,查/写窗口真实存在;
/// 内存实现同步完成、窗口为纳秒级线程抢占级,难以稳定复现——对照测试结果不证明"无此竞态")。
/// </summary>
public class AIdempotencyRaceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>高危竞态探针:每次真实执行计数(静态,跨并发任务可见)。
    /// 方法名刻意唯一(AIdemProbeAsync → 工具名 "AIdemProbe"):同程序集 ApprovalRaceTests.RaceProbeService.ProbeAsync
    /// 的工具名同为 "Probe",_rawFunctions 同名键后注册者胜出,会静默劫持批准执行的探针。</summary>
    public class AIdemPotencyProbeService
    {
        public static int ExecutionCount;

        [NexMeshTool("高危:幂等竞态探针计数,每次真实执行 +1。高危写操作!", NexMeshRiskLevel.High)]
        public Task<string> AIdemProbeAsync(long amount)
        {
            Interlocked.Increment(ref ExecutionCount);
            return Task.FromResult($"probe:{amount}");
        }
    }

    private static ServiceProvider BuildProvider(string dbPath, string store)
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        services.AddScoped<AIdemPotencyProbeService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            ["NexMesh:AI:Approval:Enabled"] = "true",
            ["NexMesh:AI:Approval:Store"] = store,
        }).Build();
        services.AddNexMeshAi(config, typeof(HighRiskFixture).Assembly, typeof(AIdemPotencyProbeService).Assembly);
        if (store == "FreeSql")
        {
            services.AddSingleton<IFreeSql>(_ => new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
                .UseAutoSyncStructure(true)
                .Build());
        }
        services.AddNexMeshApproval(config);
        return services.BuildServiceProvider();
    }

    private static readonly IReadOnlyDictionary<string, object?> ProbeArgs =
        new Dictionary<string, object?> { ["amount"] = 9L };

    /// <summary>
    /// 一轮并发突发:16/48 并发调用生产入队路径(IToolApprovalGate.EnqueueAsync,
    /// 与 ApprovalRequiredAIFunction.InvokeCoreAsync → gate.EnqueueAsync 完全同参),
    /// 返回(待审单数, 全部批准后探针执行次数)。
    /// </summary>
    private static async Task<(int PendingCount, int ExecutionsAfterApprovingAll)> RunBurstAsync(
        string dbPath, string store, int concurrency)
    {
        var sp = BuildProvider(dbPath, store);
        await using (sp)
        {
            var gate = sp.GetRequiredService<IToolApprovalGate>();
            var storeApi = sp.GetRequiredService<IApprovalStore>();
            var decisions = sp.GetRequiredService<ApprovalDecisionService>();

            var toolName = sp.GetRequiredService<INexMeshToolRegistry>()
                .DiscoverTools().First(t => t.Name.Contains("AIdemProbe", StringComparison.OrdinalIgnoreCase)).Name;

            await Parallel.ForAsync(0, concurrency, async (_, _) =>
            {
                await gate.EnqueueAsync(toolName, "高危:幂等竞态探针计数,每次真实执行 +1。高危写操作!", ProbeArgs);
            });

            var pending = await storeApi.ListPendingAsync();

            // 双执行下游证明:每张待审单都是"合法可批准"的独立单——管理员逐张批准
            // (Dashboard 审批中心逐条批准的正常操作),每张批准都会真实执行工具。
            foreach (var ticket in pending)
            {
                await decisions.ApproveAsync(ticket.Id, decidedBy: "admin-idem");
            }

            return (pending.Count, AIdemPotencyProbeService.ExecutionCount);
        }
    }

    [Fact]
    public async Task 并发同参数高危入队_真库竞态_幂等契约_重复请求只允许一张待审单()
    {
        AIdemPotencyProbeService.ExecutionCount = 0;

        // 至多 5 轮独立突发(每轮全新 store/库文件):一旦某轮产生 ≥2 张待审单即实证。
        // 轮次间无法"借力"——已存在的待审单会让后续重复请求合并,只有单轮并发突发有效。
        var lastPending = 0;
        var lastExecutions = 0;
        var reproduced = false;
        for (var round = 0; round < 5 && !reproduced; round++)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-idem-race-{Guid.CreateVersion7():N}.db");
            try
            {
                AIdemPotencyProbeService.ExecutionCount = 0; // 每轮清零:断言口径=本轮执行次数(跨轮累计会把 5 轮×1 误报成 5)
                (lastPending, lastExecutions) = await RunBurstAsync(dbPath, "FreeSql", concurrency: 48);
                reproduced = lastPending > 1;
            }
            finally
            {
                try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
            }
        }

        Assert.True(lastPending == 1,
            $"[P0 实证] 幂等契约被打破:同参数高危工具并发入队产生 {lastPending} 张待审单" +
            $"(契约要求恰好 1 张,重复请求应合并);逐张批准后探针真实执行 {lastExecutions} 次 = 双执行。");
        Assert.True(lastExecutions == 1,
            $"[P0 实证] 高危工具被执行 {lastExecutions} 次(不变量:至多 1 次)。");
    }

    /// <summary>
    /// 内存实现对照:逻辑上存在同一 TOCTOU(查→写之间无线程切换屏障),
    /// 但两步均同步完成(已完成任务 await 内联续延),窗口仅剩多核线程抢占级,
    /// 本测试预期大概率通过——通过 ≠ 无此竞态,实证以 FreeSql 真库测试为准。
    /// </summary>
    [Fact]
    public async Task 并发同参数高危入队_内存实现_对照观察()
    {
        AIdemPotencyProbeService.ExecutionCount = 0;
        var (pendingCount, executions) = await RunBurstAsync(":memory-mode:", "Memory", concurrency: 16);

        // 记录型断言:若失败(产生多张待审单)即内存实现竞态也被实证;若通过仅说明窗口未被命中。
        Assert.True(pendingCount == 1,
            $"[实证] 内存实现同样产生 {pendingCount} 张待审单(批准后执行 {executions} 次)");
    }
}
