using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI.Tools;

namespace NexMesh.AI.Tests;

public class ToolSearchTests
{
    private sealed class CountingClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static IServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
        }).Build();
        var services = new ServiceCollection();
        services.AddNexMeshAi(config, typeof(ToolSearchTests).Assembly);
        var sp = services.BuildServiceProvider();
        return sp;
    }

    [Fact]
    public async Task 工具数超过TopK_语义召回过滤()
    {
        var provider = BuildProvider();
        var registry = provider.GetRequiredService<INexMeshToolRegistry>();
        var inner = new CountingClient();
        using var search = new ToolSearchChatClient(inner, new HashEmbedder(), registry, topK: 3);

        var options = new ChatOptions
        {
            Tools = [.. registry.DiscoverTools()], // 13 个工具
        };

        // 用户问"库存"→ 应召回含"库存"的工具,而非前 3 个注册的
        var response = await search.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "批量调整库存")], options);

        Assert.True(inner.Calls > 0);
        Assert.NotNull(inner.LastOptions?.Tools);
        Assert.True(inner.LastOptions!.Tools.Count <= 3, $"预期 ≤3,实际 {inner.LastOptions.Tools.Count}");
    }

    [Fact]
    public async Task 工具数不超TopK_直接透传()
    {
        var provider = BuildProvider();
        var registry = provider.GetRequiredService<INexMeshToolRegistry>();
        var inner = new CountingClient();
        using var search = new ToolSearchChatClient(inner, new HashEmbedder(), registry, topK: 20);

        var options = new ChatOptions
        {
            Tools = [.. registry.DiscoverTools()], // 13 < 20
        };

        await search.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], options);

        // 13 ≤ 20 → 不过滤
        Assert.Equal(13, inner.LastOptions?.Tools?.Count);
    }
}
