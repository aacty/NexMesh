using Microsoft.Extensions.AI;
using NexMesh.AI.Tools;
using NexMesh.Core.Context;

namespace NexMesh.AI.SemanticCache.Tests;

/// <summary>SSS+第二轮:语义缓存租户隔离(跨租户 PHI 串答在此截断)。</summary>
public class TenantIsolationTests
{
    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }
        public string Answer { get; set; } = "默认答案";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static SemanticCacheChatClient Build(CountingClient inner)
    {
        var options = new SemanticCacheOptions { Enabled = true, Threshold = 0.85, TtlSeconds = 3600 };
        return new SemanticCacheChatClient(inner, new HashEmbedder(), new InMemorySemanticStore(), options);
    }

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public async Task 相同问题不同租户_不串答()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        // 租户 A 提问 → 模型回答"A 的答案"
        inner.Answer = "A 院的答案";
        string answerA;
        using (TenantHelper.Enter("hosp-A"))
        {
            answerA = (await cache.GetResponseAsync(Ask("患者床号3的用药方案"))).Text;
        }

        // 租户 B 提同样的问题 → 不得命中 A 的缓存,必须重新调模型
        inner.Answer = "B 院的答案";
        string answerB;
        using (TenantHelper.Enter("hosp-B"))
        {
            answerB = (await cache.GetResponseAsync(Ask("患者床号3的用药方案"))).Text;
        }

        Assert.Equal("A 院的答案", answerA);
        Assert.Equal("B 院的答案", answerB); // 若串答,这里会拿到 A 的答案
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task 同租户相同问题_正常命中()
    {
        var inner = new CountingClient();
        using var cache = Build(inner);

        string first, second;
        using (TenantHelper.Enter("hosp-A"))
        {
            first = (await cache.GetResponseAsync(Ask("怎么修改密码"))).Text;
            second = (await cache.GetResponseAsync(Ask("怎么修改密码"))).Text;
        }

        Assert.Equal(first, second);
        Assert.Equal(1, inner.Calls); // 同租户命中,模型只调一次
    }
}
