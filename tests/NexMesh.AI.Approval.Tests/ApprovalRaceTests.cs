namespace NexMesh.AI.Approval.Tests;

using System.Collections.Concurrent;
using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using NexMesh.AI.Approval;
using NexMesh.AI.Tools;
using NexMesh.AiFixtures.High;
using NexMesh.Core;
using NexMesh.Core.Context;
using System.Text.Json;

/// <summary>
/// 审批决策并发战争测试(SSS+):同一张待审单被并发批准/拒绝——
/// 原子认领语义下高危工具恰好执行至多一次,败者一律 409。
/// 判别器用 FreeSql 真库(真实 DB I/O 交出执行权,竞态窗口真实存在;
/// 内存实现同步完成,窗口趋零,不能作为竞态证据)。
/// </summary>
public class ApprovalRaceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>高危竞态探针:每次真实执行计数(静态,跨并发任务可见)。</summary>
    public class RaceProbeService
    {
        public static int ExecutionCount;

        [NexMeshTool("高危:竞态探针计数,每次真实执行 +1。高危写操作!", NexMeshRiskLevel.High)]
        public Task<string> ProbeAsync(long amount)
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
        services.AddScoped<RaceProbeService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            ["NexMesh:AI:Approval:Enabled"] = "true",
            ["NexMesh:AI:Approval:Store"] = store,
        }).Build();
        services.AddNexMeshAi(config, typeof(HighRiskFixture).Assembly, typeof(RaceProbeService).Assembly);
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

    private static async Task<string> InvokeToolAsync(
        IServiceProvider sp, string toolName, object? args = null)
    {
        var registry = sp.GetRequiredService<INexMeshToolRegistry>();
        var tool = registry.DiscoverTools().First(t => t.Name.Contains(toolName, StringComparison.OrdinalIgnoreCase));
        var arguments = args is null
            ? []
            : new AIFunctionArguments(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        var result = await tool.InvokeAsync(arguments);
        return result?.ToString() ?? "";
    }

    /// <summary>造一张待审单,返回(审批单, 清理函数)。</summary>
    private static async Task<(ApprovalRequest Request, ServiceProvider Sp)> CreatePendingAsync(string dbPath, string store)
    {
        var sp = BuildProvider(dbPath, store);
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        using (accessor.Enter(new NexMeshContext { UserId = "u-race", TenantId = "hosp-Z" }))
        {
            await InvokeToolAsync(sp, "Probe", new { amount = 9 });
        }
        var store2 = sp.GetRequiredService<IApprovalStore>();
        var pending = Assert.Single(await store2.ListPendingAsync());
        return (pending, sp);
    }

    [Fact]
    public async Task 并发批准同一审批单_真库竞态_高危工具恰好执行一次_败者409()
    {
        RaceProbeService.ExecutionCount = 0;
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-approval-race-{Guid.CreateVersion7():N}.db");
        try
        {
            var (request, sp) = await CreatePendingAsync(dbPath, "FreeSql");
            await using (sp)
            {
                var decisions = sp.GetRequiredService<ApprovalDecisionService>();
                var store = sp.GetRequiredService<IApprovalStore>();

                const int N = 16;
                var conflicts = 0;
                await Parallel.ForAsync(0, N, async (_, _) =>
                {
                    try
                    {
                        await decisions.ApproveAsync(request.Id, decidedBy: "admin-race");
                    }
                    catch (MeshException ex) when (ex.StatusCode == 409)
                    {
                        Interlocked.Increment(ref conflicts);
                    }
                });

                var final = await store.GetAsync(request.Id);
                Assert.Equal(1, RaceProbeService.ExecutionCount); // 不变量:恰好一次,绝不双执行
                Assert.Equal(N - 1, conflicts);                    // 败者全部 409(无静默双胜)
                Assert.Equal(ApprovalStatus.Executed, final!.Status);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
        }
    }

    [Fact]
    public async Task 并发批准与拒绝混合_真库竞态_终态唯一_执行至多一次()
    {
        RaceProbeService.ExecutionCount = 0;
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-approval-mixed-{Guid.CreateVersion7():N}.db");
        try
        {
            var (request, sp) = await CreatePendingAsync(dbPath, "FreeSql");
            await using (sp)
            {
                var decisions = sp.GetRequiredService<ApprovalDecisionService>();
                var store = sp.GetRequiredService<IApprovalStore>();

                const int N = 16;
                var conflicts = 0;
                await Parallel.ForAsync(0, N, async (w, _) =>
                {
                    try
                    {
                        if (w % 2 == 0)
                        {
                            await decisions.ApproveAsync(request.Id, decidedBy: "admin-a");
                        }
                        else
                        {
                            await decisions.RejectAsync(request.Id, decidedBy: "admin-b");
                        }
                    }
                    catch (MeshException ex) when (ex.StatusCode == 409)
                    {
                        Interlocked.Increment(ref conflicts);
                    }
                });

                var final = await store.GetAsync(request.Id);
                Assert.True(RaceProbeService.ExecutionCount <= 1, $"执行次数 {RaceProbeService.ExecutionCount} > 1(双执行)");
                Assert.Equal(N - 1, conflicts); // 恰好一人胜出
                Assert.True(
                    final!.Status is ApprovalStatus.Rejected or ApprovalStatus.Executed or ApprovalStatus.Failed,
                    $"终态非法: {final.Status}");
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
        }
    }

    [Fact]
    public async Task 并发批准_内存实现_CAS认领_恰好执行一次()
    {
        RaceProbeService.ExecutionCount = 0;
        var (request, sp) = await CreatePendingAsync(":memory-mode:", "Memory");
        await using (sp)
        {
            var decisions = sp.GetRequiredService<ApprovalDecisionService>();
            var store = sp.GetRequiredService<IApprovalStore>();

            const int N = 16;
            var conflicts = 0;
            await Parallel.ForAsync(0, N, async (_, _) =>
            {
                try
                {
                    await decisions.ApproveAsync(request.Id, decidedBy: "admin-mem");
                }
                catch (MeshException ex) when (ex.StatusCode == 409)
                {
                    Interlocked.Increment(ref conflicts);
                }
            });

            var final = await store.GetAsync(request.Id);
            Assert.True(RaceProbeService.ExecutionCount <= 1, $"执行次数 {RaceProbeService.ExecutionCount} > 1");
            Assert.Equal(N - 1, conflicts);
            Assert.Equal(ApprovalStatus.Executed, final!.Status);
        }
    }
}
