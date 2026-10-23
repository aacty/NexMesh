using NexMesh.AI.Tools;
namespace NexMesh.AI.SemanticCache;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// 语义缓存装饰器:用户提问 → 嵌入向量 → 余弦相似检索 →
/// 命中(≥ 阈值)秒回上次回答(跳过模型调用,成本直降);未命中 → 调用模型 → 存入向量缓存。
/// 仅覆盖非流式调用(流式首 token 语义无法完整比对);仅缓存成功响应。
/// </summary>
public sealed class SemanticCacheChatClient(
    IChatClient inner,
    ISemanticEmbedder embedder,
    ISemanticVectorStore store,
    SemanticCacheOptions options,
    ILogger<SemanticCacheChatClient>? logger = null) : DelegatingChatClient(inner)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>SSS+安全:租户隔离前缀——缓存键与检索均限定租户,杜绝跨院区 PHI 串答。</summary>
    private string TenantPrefix => string.IsNullOrEmpty(TenantId) ? "" : $"{TenantId}:";
    private static string? TenantId => NexMesh.Core.Context.TenantHelper.Current;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        var query = ExtractUserText(messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            return await base.GetResponseAsync(messages, chatOptions, cancellationToken);
        }

        // ① 嵌入 → ② 相似检索(scope=租户前缀,只匹配同租户条目) → ③ 命中即秒回
        var vector = await embedder.EmbedAsync(query, cancellationToken);
        var match = await store.SearchAsync(TenantPrefix, vector, options.Threshold, cancellationToken);
        if (match is not null)
        {
            logger?.LogInformation("语义缓存命中 {Similarity:F3}(Key={Key}),跳过模型调用", match.Similarity, match.Key);
            var cached = System.Text.Json.JsonSerializer.Deserialize<CachedResponse>(match.Payload, Json);
            if (cached is not null)
            {
                return cached.ToChatResponse();
            }
        }

        // ④ 未命中:调用模型 → ⑤ 存入缓存(键带租户前缀,隔离跨院区)
        var response = await base.GetResponseAsync(messages, chatOptions, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text))
        {
            var payload = new CachedResponse(response.Text, response.ModelId ?? "").ToJson();
            await store.AddAsync(
                TenantPrefix, $"q:{Guid.CreateVersion7():N}", vector, payload,
                options.TtlSeconds > 0 ? TimeSpan.FromSeconds(options.TtlSeconds) : null,
                cancellationToken);
        }
        return response;
    }

    /// <summary>流式不缓存(首 token 语义无法完整比对;且流式已在弹性层有首 token 守卫)。</summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            yield return update;
        }
    }

    private static string ExtractUserText(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    private sealed record CachedResponse(string Text, string ModelId)
    {
        public ChatResponse ToChatResponse() => new(new ChatMessage(ChatRole.Assistant, Text))
        {
            ModelId = ModelId,
        };

        public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Json);
    }
}
