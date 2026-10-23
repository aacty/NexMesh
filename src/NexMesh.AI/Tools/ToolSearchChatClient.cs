namespace NexMesh.AI.Tools;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// 语义向量生成(嵌入):业务文本 → 高维向量(余弦相似度衡量语义接近度)。
/// ToolSearch(工具语义召回)与 SemanticCache(语义缓存)共用此抽象。
/// </summary>
public interface ISemanticEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Tool Search 语义召回装饰器:替代 MaxToolsPerRequest 硬截断——
/// 嵌入用户消息 → 对全部工具 Description 计算余弦相似 → 按相似度降序取 Top-K。
/// 千级工具场景下只送最相关的 K 个进模型 Prompt(截断送的是"前 K 个注册的",语义无关)。
/// 工具描述向量启动时预计算缓存(单例生命周期,注册即嵌入)。
/// </summary>
public sealed class ToolSearchChatClient(
    IChatClient inner,
    ISemanticEmbedder embedder,
    INexMeshToolRegistry toolRegistry,
    int topK = 5) : DelegatingChatClient(inner)
{
    private Lazy<Task<List<(AIFunction Tool, float[] Vector)>>>? _toolVectors;

    /// <summary>线程安全惰性缓存:Lazy 保证并发首请求只计算一次(真实 embedder 不重复计费)。</summary>
    private Task<List<(AIFunction, float[])>> GetToolVectorsAsync(CancellationToken ct) =>
        (_toolVectors ??= new Lazy<Task<List<(AIFunction, float[])>>>(
            () => ComputeToolVectorsAsync(CancellationToken.None))).Value; // 预热用独立令牌:首个请求取消不中断共享缓存

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        chatOptions = await FilterToolsAsync(messages, chatOptions, cancellationToken);
        return await base.GetResponseAsync(messages, chatOptions, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        chatOptions = await FilterToolsAsync(messages, chatOptions, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>语义召回:嵌入用户消息 → 与工具描述向量比对 → 按相似度取 Top-K。</summary>
    private async Task<ChatOptions?> FilterToolsAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        if (options?.Tools is not { Count: > 0 } || options.Tools.Count <= topK)
        {
            return options; // 工具数 ≤ topK 不需要召回
        }

        var query = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return options; // 无用户消息 → 回退截断
        }

        // 惰性预计算:全部工具 Description 的向量(Lazy 单飞缓存)
        var toolVectors = await GetToolVectorsAsync(ct);
        if (toolVectors.Count == 0)
        {
            return options; // 无工具 → 不过滤
        }

        // 嵌入用户消息 → 余弦排序 → Top-K
        var queryVector = await embedder.EmbedAsync(query, ct);
        var ranked = toolVectors
            .Select(tv => (tv.Item1, Score: Cosine(queryVector, tv.Item2)))
            .Where(tv => tv.Score > 0.1) // 语义无关的过滤掉
            .OrderByDescending(tv => tv.Score)
            .Take(topK)
            .Select(tv => tv.Item1)
            .ToList();

        if (ranked.Count == 0)
        {
            return options; // 全部低相似 → 回退(不做灾难性过滤)
        }

        // 1f:克隆 ChatOptions 再改写——不变异调用方实例(复用同一 options 的并发场景安全)
        var filtered = options.Clone();
        filtered.Tools = [.. ranked];
        return filtered;
    }

    private async Task<List<(AIFunction, float[])>> ComputeToolVectorsAsync(CancellationToken ct)
    {
        // 全量工具(不经 MaxToolsPerRequest 截断):千级工具召回必须覆盖全部注册工具
        var tools = toolRegistry is NexMeshToolRegistry registry
            ? registry.DiscoverAllTools()
            : toolRegistry.DiscoverTools().ToList();
        var result = new List<(AIFunction, float[])>(tools.Count);
        foreach (var tool in tools)
        {
            var vector = await embedder.EmbedAsync(tool.Description ?? tool.Name, ct);
            result.Add((tool, vector));
        }
        return result;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
