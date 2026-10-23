namespace NexMesh.AI.Tests;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI;
using NexMesh.AI.Core;

public class FactoryTests
{
    private static IConfiguration BuildStubConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:DefaultProvider"] = "main",
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
        }).Build();

    private static (ServiceProvider Sp, INexMeshChatFactory Factory) BuildFactory()
    {
        var config = BuildStubConfig();
        var services = new ServiceCollection();
        // 传入 NexMesh.AI 自身程序集(无任何工具)——安全门验证可通过
        services.AddNexMeshAi(config, typeof(NexMeshAiServiceCollectionExtensions).Assembly);
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<INexMeshChatFactory>());
    }

    [Fact]
    public void 字典缓存_同一Provider两次获取为同实例()
    {
        var (sp, factory) = BuildFactory();
        using var _ = sp;

        var c1 = factory.CreateClient();
        var c2 = factory.CreateClient();

        Assert.Same(c1, c2);
    }

    [Fact]
    public void 按名称解析_与默认Provider同实例()
    {
        var (sp, factory) = BuildFactory();
        using var _ = sp;

        Assert.Same(factory.CreateClient(), factory.CreateClient("main"));
    }

    [Fact]
    public void 未知Provider_抛InvalidOperationException()
    {
        var (sp, factory) = BuildFactory();
        using var _ = sp;

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient("nonexistent"));
    }

    [Fact]
    public async Task StubProvider_可完成对话调用()
    {
        var (sp, factory) = BuildFactory();
        using var _ = sp;

        var response = await factory.CreateClient().GetResponseAsync(
            [new ChatMessage(ChatRole.User, "你好")]);

        Assert.Contains("[NexMesh Stub:stub-model]", response.Text);
    }

    [Fact]
    public async Task StubProvider_流式输出可枚举()
    {
        var (sp, factory) = BuildFactory();
        using var _ = sp;

        var text = new System.Text.StringBuilder();
        await foreach (var update in factory.CreateClient().GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "你好")]))
        {
            text.Append(update.Text);
        }

        Assert.Contains("离线流式", text.ToString());
    }
}
