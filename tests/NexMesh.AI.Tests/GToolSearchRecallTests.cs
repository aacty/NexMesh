namespace NexMesh.AI.Tests;

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using NexMesh.AI.Tests.Fixtures;
using NexMesh.AI.Tools;

/// <summary>
/// G(架构师二轮交叉测试)ToolSearch 语义召回:
/// 相近工具名排序确定性(并列分数不抖动)、空/无用户消息/超长查询不炸、
/// 全部低相似时回退不灾难性过滤、caller 的 ChatOptions 不被变异。
/// </summary>
public class GToolSearchRecallTests
{
    /// <summary>恒定向量嵌入器:所有文本同向量 → 全部工具并列满分(排序稳定性攻击面)。</summary>
    private sealed class ConstantEmbedder : ISemanticEmbedder
    {
        public int Calls { get; private set; }
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            var vector = new float[256];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }

    /// <summary>零向量嵌入器:与任何工具描述向量正交 → 相似度全部 0(灾难性过滤攻击面)。</summary>
    private sealed class ZeroEmbedder : ISemanticEmbedder
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[256]);
    }

    /// <summary>记录收到的 options 实例与工具清单的内层客户端。</summary>
    private sealed class RecordingClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public ChatOptions? OptionsReference { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            OptionsReference = options;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static NexMeshToolRegistry BuildRegistry()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
        }).Build();
        return new NexMeshToolRegistry(config, typeof(LowTools).Assembly);
    }

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    private static IReadOnlyList<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.Select(t => t.Name).ToList() ?? [];

    // ── 相近工具名排序确定性 ─────────────────────────────────────────

    [Fact]
    public async Task 相近工具名_独占字符查询置顶_重复调用与跨实例排序稳定()
    {
        var registry = BuildRegistry();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, new HashEmbedder(), registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        var seen = new List<IReadOnlyList<string>>();
        for (var round = 0; round < 5; round++)
        {
            await search.GetResponseAsync(Ask("批量调整库存(生效日期+数量)"), options);
            var ranked = ToolNames(inner.LastOptions);
            Assert.True(ranked.Count <= 3);
            Assert.Equal("Restock", ranked[0]); // 与描述逐字一致 → 相似度精确 1.0,决定性置顶
            seen.Add(ranked);
        }

        // 同一实例重复请求:同分/近分排序不得抖动
        Assert.All(seen, ranked => Assert.Equal(seen[0], ranked));

        // 全新实例:排序同样确定(不依赖实例内部的偶然状态)
        var inner2 = new RecordingClient();
        using var search2 = new ToolSearchChatClient(inner2, new HashEmbedder(), registry, topK: 3);
        await search2.GetResponseAsync(Ask("批量调整库存(生效日期+数量)"), options);
        Assert.Equal(seen[0], ToolNames(inner2.LastOptions));
    }

    [Fact]
    public async Task HashEmbedder浮点噪声实证_单字符差异的排序倒挂与余弦超1()
    {
        // 实证发现(钉死现状):HashEmbedder 归一化后,余弦用 float 乘积累加(double),
        // 乘积舍入噪声(~1e-7)支配单字符差异信号(~1e-9)——
        // 近似文本(演示工具03)余弦可达 1.0000000046 > 1,而与查询完全一致的文本(演示工具07)恰为 1.0,
        // 被压到第二。检索排序对"逐字近重复"的区分在噪声层失效;阈值 0.85 级决策不受影响。
        var embedder = new HashEmbedder();
        var query = await embedder.EmbedAsync("演示工具07");
        var exact = await embedder.EmbedAsync("演示工具07");
        var near = await embedder.EmbedAsync("演示工具03");

        Assert.Equal(query, exact); // 相同文本 → 向量逐位相同(契约保持)
        var exactScore = Cos(query, exact);
        var nearScore = Cos(query, near);
        Assert.Equal(1.0, exactScore, precision: 12); // 自身余弦=1(double 重算的舍入在 1e-16 级)
        // 【修复反转】内容哈希折叠后近重复文本向量实质不同:余弦 ≤ 1,精确匹配居首
        //(修复前实证:纯位置分桶下单字符差异被 float 舍入噪声淹没,近重复余弦 1.0000000046 倒挂)
        Assert.True(nearScore <= 1.0 + 1e-9, $"近重复文本余弦 {nearScore:R} 不应超过 1(内容哈希已区分)");
        Assert.True(exactScore > nearScore, "精确匹配应高于近重复文本(排序无倒挂)");
    }

    private static double Cos(float[] x, float[] y)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += x[i] * y[i];
            na += x[i] * x[i];
            nb += y[i] * y[i];
        }
        return dot == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [Fact]
    public async Task 全部工具并列满分_保持注册顺序_跨实例确定()
    {
        var registry = BuildRegistry();
        var registrationOrder = registry.DiscoverAllTools().Select(t => t.Name).ToList();
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        var embedder = new ConstantEmbedder();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, embedder, registry, topK: 3);
        await search.GetResponseAsync(Ask("任何问题"), options);

        var ranked = ToolNames(inner.LastOptions);
        // LINQ OrderByDescending 稳定排序:并列分数保持注册顺序,Top-3 = 注册顺序前 3
        Assert.Equal(registrationOrder.Take(3), ranked);

        // 第二个全新实例同构 → 同结果(不因字典/哈希顺序漂移)
        var inner2 = new RecordingClient();
        using var search2 = new ToolSearchChatClient(inner2, new ConstantEmbedder(), registry, topK: 3);
        await search2.GetResponseAsync(Ask("任何问题"), options);
        Assert.Equal(ranked, ToolNames(inner2.LastOptions));
        Assert.True(embedder.Calls > 0);
    }

    // ── 空查询 / 无用户消息 / 超长查询 ───────────────────────────────

    [Fact]
    public async Task 无用户消息_透传不过滤且不触发嵌入()
    {
        var registry = BuildRegistry();
        var embedder = new ConstantEmbedder();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, embedder, registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        await search.GetResponseAsync([new ChatMessage(ChatRole.System, "只有系统消息")], options);

        Assert.Equal(13, inner.LastOptions?.Tools?.Count); // 全量透传
        Assert.Equal(0, embedder.Calls); // 未触发嵌入(无用户消息直接回退)
    }

    [Fact]
    public async Task 空查询与纯空白查询_透传不炸()
    {
        var registry = BuildRegistry();
        var embedder = new ConstantEmbedder();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, embedder, registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        await search.GetResponseAsync(Ask(""), options);
        Assert.Equal(13, inner.LastOptions?.Tools?.Count);

        await search.GetResponseAsync(Ask("   \t\n  "), options);
        Assert.Equal(13, inner.LastOptions?.Tools?.Count);
    }

    [Fact]
    public async Task 超长查询_不崩溃_仍返回TopK()
    {
        var registry = BuildRegistry();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, new HashEmbedder(), registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        // 50 万字符级查询:真实场景极端 Prompt 注入/粘贴事故
        var huge = string.Concat(Enumerable.Repeat("批量调整库存", 80000));
        Assert.True(huge.Length > 400_000);

        var watch = Stopwatch.StartNew();
        await search.GetResponseAsync(Ask(huge), options);
        watch.Stop();

        // 不崩溃、仍按 TopK 截断、且排序确定(重复请求同序)——超长查询下不保证特定工具置顶
        var first = ToolNames(inner.LastOptions);
        Assert.True(first.Count is > 0 and <= 3, $"预期 1~3 个工具,实际 {first.Count}");
        await search.GetResponseAsync(Ask(huge), options);
        Assert.Equal(first, ToolNames(inner.LastOptions));
        Assert.True(watch.ElapsedMilliseconds < 30_000, $"超长查询耗时 {watch.ElapsedMilliseconds}ms");
    }

    // ── 边界与安全回退 ───────────────────────────────────────────────

    [Fact]
    public async Task 工具数恰好等于TopK_透传且不触发嵌入()
    {
        var registry = BuildRegistry();
        var embedder = new ConstantEmbedder();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, embedder, registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools().Take(3)] };

        await search.GetResponseAsync(Ask("批量调整库存"), options);

        Assert.Equal(3, inner.LastOptions?.Tools?.Count); // ≤ topK 直接透传
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public async Task 全部低相似_回退全量而非灾难性过滤()
    {
        var registry = BuildRegistry();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, new ZeroEmbedder(), registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        await search.GetResponseAsync(Ask("批量调整库存"), options);

        // 相似度全部 ≤ 0.1 → ranked 空 → 必须回退全量,不能把工具过滤到 0
        Assert.Equal(13, inner.LastOptions?.Tools?.Count);
    }

    [Fact]
    public async Task 召回过滤_克隆ChatOptions_不变异调用方实例()
    {
        var registry = BuildRegistry();
        var inner = new RecordingClient();
        using var search = new ToolSearchChatClient(inner, new HashEmbedder(), registry, topK: 3);
        var options = new ChatOptions { Tools = [.. registry.DiscoverAllTools()] };

        await search.GetResponseAsync(Ask("批量调整库存"), options);

        Assert.Equal(13, options.Tools!.Count); // 调用方的 options 不被改写
        Assert.NotSame(options, inner.OptionsReference); // 过滤后返回克隆
        Assert.True(inner.OptionsReference!.Tools!.Count <= 3);
    }
}
