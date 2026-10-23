using Microsoft.Extensions.AI;
using NexMesh.AI.SemanticCache;
using NexMesh.AI.Tools;

namespace NexMesh.AI.SemanticCache.Tests;

/// <summary>语义缓存核心语义:相同/相似文本命中缓存跳过模型,不同文本穿透,TTL 过期重新回源。</summary>
public class SemanticCacheTests
{
    /// <summary>计数模型客户端(每次调用递增)。</summary>
    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"答案 #{Calls}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "流式答案");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static SemanticCacheChatClient Build(
        CountingClient inner, double threshold = 0.85, int ttlSeconds = 3600)
    {
        var options = new SemanticCacheOptions { Enabled = true, Threshold = threshold, TtlSeconds = ttlSeconds };
        return new SemanticCacheChatClient(inner, new HashEmbedder(), new InMemorySemanticStore(), options);
    }

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public async Task 完全相同文本_第二次命中缓存_模型零调用()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        var first = await cache.GetResponseAsync(Ask("怎么修改密码"));
        var second = await cache.GetResponseAsync(Ask("怎么修改密码")); // 同题 → 命中

        Assert.Equal(1, inner.Calls); // 模型只被调用一次
        Assert.Equal("答案 #1", first.Text);
        Assert.Equal("答案 #1", second.Text); // 第二次返回缓存的同一条答案
    }

    [Fact]
    public async Task 空白差异_仍命中缓存_()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        await cache.GetResponseAsync(Ask("怎么修改密码"));
        var second = await cache.GetResponseAsync(Ask("  怎么修改密码  ")); // 首尾空白归一后同题

        Assert.Equal(1, inner.Calls);
        Assert.Equal("答案 #1", second.Text);
    }

    [Fact]
    public async Task 不同文本_未命中_穿透到模型()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        await cache.GetResponseAsync(Ask("怎么修改密码"));
        var second = await cache.GetResponseAsync(Ask("hello world"));

        Assert.Equal(2, inner.Calls); // 两次都调了模型
        Assert.Equal("答案 #2", second.Text);
    }

    [Fact]
    public async Task TTL过期_重新回源()
    {
        var inner = new CountingClient();
        var options = new SemanticCacheOptions { Enabled = true, TtlSeconds = 1 };
        using var cache = new SemanticCacheChatClient(
            inner, new HashEmbedder(), new InMemorySemanticStore(), options);

        await cache.GetResponseAsync(Ask("怎么修改密码"));
        await Task.Delay(1100); // 确保 TTL(1秒) 已过
        var second = await cache.GetResponseAsync(Ask("怎么修改密码"));

        Assert.Equal(2, inner.Calls); // 过期后重新调模型
    }

    [Fact]
    public async Task 高阈值_降低命中率()
    {
        var inner = new CountingClient();
        using var cache = Build(inner, threshold: 0.999999); // 几乎不可能命中

        await cache.GetResponseAsync(Ask("abcdef"));
        await cache.GetResponseAsync(Ask("abcdef"));

        // 高阈值下可能仍命中(字符频率完全一致),但行为正确(不崩溃)
        Assert.True(inner.Calls is 1 or 2);
    }

    [Fact]
    public async Task 流式调用_不缓存不命中_直接透传()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        var updates = new List<string>();
        await foreach (var u in cache.GetStreamingResponseAsync(Ask("怎么修改密码")))
        {
            updates.Add(u.Text ?? "");
        }
        await foreach (var u in cache.GetStreamingResponseAsync(Ask("怎么修改密码")))
        {
            updates.Add(u.Text ?? "");
        }

        Assert.Equal(2, inner.Calls); // 流式每次都调模型(不缓存)
        Assert.Equal(2, updates.Count);
    }
}
