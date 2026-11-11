namespace NexMesh.AI.Approval.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.AI.Approval;
using NexMesh.AI.Tools;
using NexMesh.AiFixtures.High;
using NexMesh.Core;

/// <summary>
/// 工具参数 Schema 回归(框架架构师业务实测发现,修复锚定):
/// 审批包装器(ApprovalRequiredAIFunction)与实例工具(InstanceToolAIFunction)此前的
/// JsonSchema 均为基类默认空参数对象——真模型看到的工具全部"无参",无法传参
/// (实弹:DeepSeek 调 QueryOrder/ForceCloseOrder 因无 schema 被框架拒绝)。
/// 修复 = 实例工具按 MethodInfo 构建 schema + 审批包装器全量转发 raw 元数据。
/// </summary>
public class AToolSchemaTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        services.AddScoped<ApprovalRaceTests.RaceProbeService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            ["NexMesh:AI:Approval:Enabled"] = "true",
            ["NexMesh:AI:Approval:Store"] = "Memory",
        }).Build();
        services.AddNexMeshAi(config, typeof(HighRiskFixture).Assembly, typeof(ApprovalRaceTests.RaceProbeService).Assembly);
        services.AddNexMeshApproval(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void 审批包装后_参数schema仍完整暴露给模型()
    {
        using var sp = BuildProvider();
        var registry = sp.GetRequiredService<INexMeshToolRegistry>();

        var wrapped = registry.DiscoverTools().First(t => t.Name == "RaceProbe");
        var properties = wrapped.JsonSchema.GetProperty("properties");
        Assert.Contains("amount", properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal("integer", properties.GetProperty("amount").GetProperty("type").GetString());
    }

    [Fact]
    public void 实例工具schema_与raw一致_包装纯转发()
    {
        using var sp = BuildProvider();
        var registry = sp.GetRequiredService<INexMeshToolRegistry>();

        var wrapped = registry.DiscoverTools().First(t => t.Name == "RaceProbe");
        var raw = registry.FindRawFunction("RaceProbe");
        Assert.NotNull(raw);
        Assert.Equal(raw!.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText()); // 包装器纯转发,模型看到的 schema 与 raw 逐字节一致
    }
}
