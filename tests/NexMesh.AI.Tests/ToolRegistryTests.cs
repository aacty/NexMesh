namespace NexMesh.AI.Tests;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AiFixtures.High;
using NexMesh.AiFixtures.Instance;
using NexMesh.AI.Tests.Fixtures;
using NexMesh.AI.Tools;

public class ToolRegistryTests
{
    private static IConfiguration Config(int? maxTools = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
        };
        if (maxTools is not null)
        {
            dict["NexMesh:AI:MaxToolsPerRequest"] = maxTools.ToString();
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void 静态低风险工具_全部发现并保留Description()
    {
        var tools = new NexMeshToolRegistry(Config(maxTools: 100), typeof(LowTools).Assembly).DiscoverTools().ToList();

        Assert.Equal(13, tools.Count);
        Assert.Contains(tools, t => t.Description == "回显工具:原样返回输入文本");
    }

    [Fact]
    public void 超过MaxToolsPerRequest_确定性截断()
    {
        var tools = new NexMeshToolRegistry(Config(maxTools: 5), typeof(LowTools).Assembly).DiscoverTools().ToList();

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public async Task 工具可调用_参数正确绑定()
    {
        var tools = new NexMeshToolRegistry(Config(maxTools: 100), typeof(LowTools).Assembly).DiscoverTools();
        var echo = tools.First(t => t.Name.Contains("Echo", StringComparison.OrdinalIgnoreCase));

        // M.E.AI 将工具返回值编组为 JsonElement
        var result = await echo.InvokeAsync(new AIFunctionArguments { ["text"] = "hello nexmesh" });
        var json = Assert.IsType<System.Text.Json.JsonElement>(result);

        Assert.Equal("hello nexmesh", json.GetString());
    }

    [Fact]
    public void High风险工具_发现即抛安全门异常()
    {
        var registry = new NexMeshToolRegistry(Config(), typeof(HighRiskFixture).Assembly);

        var ex = Assert.Throws<NotSupportedException>(() => registry.DiscoverTools());
        Assert.Contains("NexMesh Security Gate", ex.Message);
    }

    [Fact]
    public async Task 实例方法工具_一等公民_经DI作用域解析调用()
    {
        var services = new ServiceCollection();
        services.AddScoped<IProbeDependency, ProbeDependency>();
        services.AddNexMeshAi(Config(maxTools: 100), typeof(InstanceToolFixture).Assembly);
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<INexMeshToolRegistry>();
        var probe = registry.DiscoverTools().First(t => t.Name == "Probe");
        var first = await probe.InvokeAsync(new AIFunctionArguments());
        var second = await probe.InvokeAsync(new AIFunctionArguments());

        // 两次调用返回不同依赖实例 Id → 每次调用都在独立 DI 作用域内解析,生命周期由框架托管
        Assert.NotEqual(first?.ToString(), second?.ToString());
        Assert.NotEqual(string.Empty, first?.ToString());
    }

    [Fact]
    public void AddNexMeshAi注册期_High工具阻断启动()
    {
        var services = new ServiceCollection();

        Assert.Throws<NotSupportedException>(() =>
            services.AddNexMeshAi(Config(), typeof(HighRiskFixture).Assembly));
    }
}

/// <summary>架构约束测试:NexMesh.AI 核心层零引用 FreeSql/EFCore/Redis(铁律固化)。</summary>
public class ArchitectureConstraintTests
{
    [Fact]
    public void NexMesh_AI程序集引用中无FreeSql_EFCore_Redis()
    {
        var references = typeof(NexMeshToolAttribute).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.DoesNotContain(references, name =>
            name.Contains("freesql", StringComparison.OrdinalIgnoreCase)
            || name.Contains("entityframework", StringComparison.OrdinalIgnoreCase)
            || name.Contains("redis", StringComparison.OrdinalIgnoreCase));
    }
}
