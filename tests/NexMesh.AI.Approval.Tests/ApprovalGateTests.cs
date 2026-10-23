namespace NexMesh.AI.Approval.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using NexMesh.AI.Approval;
using NexMesh.AI.Tools;
using NexMesh.AiFixtures.High;
using NexMesh.AiFixtures.Instance;
using NexMesh.Core;
using NexMesh.Core.Context;
using System.Text.Json;

/// <summary>
/// 审批门语义测试:挂起不执行/幂等合并/批准后按快照再水化执行(身份租户不变)/拒绝/重复决策 409。
/// </summary>
public class ApprovalGateTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>高危实例工具:以"当前用户身份"执行——批准后应读到触发时刻的快照身份。</summary>
    public class HighRiskUserService(ICurrentUser user)
    {
        public static string? LastOperator;

        [NexMeshTool("高危:以当前用户身份执行资金划转。高危写操作!", NexMeshRiskLevel.High)]
        public Task<string> TransferAsync(long amount)
            => Task.FromResult(LastOperator = $"operator={user.UserId ?? "anonymous"},tenant={user.TenantId ?? "-"},amount={amount}");
    }

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            ["NexMesh:AI:Approval:Enabled"] = "true",
            ["NexMesh:AI:Approval:Store"] = "Memory",
        }).Build();

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        services.AddScoped<HighRiskUserService>();
        var config = BuildConfig();
        services.AddNexMeshAi(config, typeof(HighRiskFixture).Assembly, typeof(HighRiskUserService).Assembly);
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

    [Fact]
    public async Task 高危工具调用_挂起不执行且入队()
    {
        using var sp = BuildProvider();
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        var store = sp.GetRequiredService<IApprovalStore>();

        string pendingText;
        using (accessor.Enter(new NexMeshContext { UserId = "doc-zhang", TenantId = "hosp-A" }))
        {
            pendingText = await InvokeToolAsync(sp, "ForceRefund", new { orderId = "O-1", reason = "test" });
        }

        Assert.Contains("[NexMesh Approval Gate]", pendingText);
        Assert.Contains("已挂起", pendingText);
        var pending = await store.ListPendingAsync();
        var request = Assert.Single(pending);
        Assert.Equal("ForceRefund", request.ToolName);
        Assert.Equal(ApprovalStatus.Pending, request.Status);
        // 快照随队落地:身份与租户进入审批单
        var headers = request.ContextHeaders;
        Assert.Equal("doc-zhang", headers["nm-user-id"]);
        Assert.Equal("hosp-A", headers["nm-tenant-id"]);
    }

    [Fact]
    public async Task 同参数重复调用_幂等合并为同一审批单()
    {
        using var sp = BuildProvider();
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        var store = sp.GetRequiredService<IApprovalStore>();

        using (accessor.Enter(new NexMeshContext { UserId = "doc-li", TenantId = "hosp-B" }))
        {
            await InvokeToolAsync(sp, "ForceRefund", new { orderId = "O-9", reason = "dup" });
            var second = await InvokeToolAsync(sp, "ForceRefund", new { orderId = "O-9", reason = "dup" });
            Assert.Contains("已在等待中", second); // 幂等合并文本
        }

        Assert.Single(await store.ListPendingAsync());
    }

    [Fact]
    public async Task 批准后_按快照再水化执行_身份租户不变()
    {
        using var sp = BuildProvider();
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        var store = sp.GetRequiredService<IApprovalStore>();
        HighRiskUserService.LastOperator = null;
        var decisions = sp.GetRequiredService<ApprovalDecisionService>();

        using (accessor.Enter(new NexMeshContext { UserId = "doc-wang", TenantId = "hosp-C" }))
        {
            await InvokeToolAsync(sp, "Transfer", new { amount = 500 });
        }
        var request = Assert.Single(await store.ListPendingAsync());
        Assert.Null(HighRiskUserService.LastOperator); // 批准前从未执行

        var decided = await decisions.ApproveAsync(request.Id, decidedBy: "admin-ma");

        Assert.Equal(ApprovalStatus.Executed, decided.Status);
        Assert.Equal("operator=doc-wang,tenant=hosp-C,amount=500", HighRiskUserService.LastOperator);
        Assert.Null(decided.Error);
    }

    [Fact]
    public async Task 拒绝_永不执行()
    {
        using var sp = BuildProvider();
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        var store = sp.GetRequiredService<IApprovalStore>();
        HighRiskUserService.LastOperator = null;
        var decisions = sp.GetRequiredService<ApprovalDecisionService>();

        using (accessor.Enter(new NexMeshContext { UserId = "u-reject", TenantId = "hosp-D" }))
        {
            await InvokeToolAsync(sp, "Transfer", new { amount = 1 });
        }
        var request = Assert.Single(await store.ListPendingAsync());

        var decided = await decisions.RejectAsync(request.Id, decidedBy: "admin-x");

        Assert.Equal(ApprovalStatus.Rejected, decided.Status);
        Assert.DoesNotContain("u-reject", HighRiskUserService.LastOperator ?? "");
    }

    [Fact]
    public async Task 重复决策_409业务码()
    {
        using var sp = BuildProvider();
        var accessor = sp.GetRequiredService<INexMeshContextAccessor>();
        var store = sp.GetRequiredService<IApprovalStore>();
        HighRiskUserService.LastOperator = null;
        var decisions = sp.GetRequiredService<ApprovalDecisionService>();

        using (accessor.Enter(new NexMeshContext { UserId = "u-409", TenantId = "hosp-E" }))
        {
            await InvokeToolAsync(sp, "Transfer", new { amount = 2 });
        }
        var request = Assert.Single(await store.ListPendingAsync());
        await decisions.ApproveAsync(request.Id, decidedBy: "admin-y");

        var ex = await Assert.ThrowsAsync<MeshException>(
            () => decisions.ApproveAsync(request.Id, decidedBy: "admin-y"));
        Assert.Equal(409, ex.StatusCode);
    }
}
