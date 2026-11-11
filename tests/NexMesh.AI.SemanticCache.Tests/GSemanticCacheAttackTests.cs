namespace NexMesh.AI.SemanticCache.Tests;

using Microsoft.Extensions.AI;
using NexMesh.AI.SemanticCache;
using NexMesh.AI.Tools;
using NexMesh.Core.Context;

/// <summary>
/// G(架构师二轮交叉测试)语义缓存正确性攻击:
/// ① HashEmbedder 对抗碰撞(>256 字符文本的 256 维分桶按位置求和 → 分块交换/分桶和相等的不同文本得到同一向量,
///    违反其"不同文本→不同向量"的声明 → 缓存错配,不同问题返回他人答案);
/// ② 匿名(无租户)上下文可命中任意租户的缓存条目(空 scope 在存储层跳过前缀过滤 → 跨租户 PHI 泄漏);
/// ③ 相似度恰好等于阈值的边界(必须命中,>= 语义);
/// ④ 同 key 并发写(无撕裂、payload 完整性)。
/// 本类只实证与钉死缺陷现状,src 修复裁决权在主裁决架构师。
/// </summary>
public class GSemanticCacheAttackTests
{
    // ── 测试替身 ─────────────────────────────────────────────────────

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
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>按文本查表返回固定向量(阈值边界用,余弦值确定可预算)。</summary>
    private sealed class PairEmbedder(string textA, float[] vectorA, string textB, float[] vectorB) : ISemanticEmbedder
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text == textA ? vectorA : text == textB ? vectorB : new float[vectorA.Length]);
    }

    private static SemanticCacheChatClient Build(
        CountingClient inner, ISemanticEmbedder? embedder = null, double threshold = 0.85, int ttlSeconds = 3600) =>
        new(inner, embedder ?? new HashEmbedder(), new InMemorySemanticStore(),
            new SemanticCacheOptions { Enabled = true, Threshold = threshold, TtlSeconds = ttlSeconds });

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    /// <summary>与 InMemorySemanticStore.Cosine 完全相同的运算次序(float 乘法、double 累加、同序除法),
    /// 用于构造"恰好等于阈值"的确定值——依赖 double 运算的位级确定性。</summary>
    private static double ReplicaCosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    // ── ① HashEmbedder 对抗碰撞 ─────────────────────────────────────

    private static string Padded(string seed, int length)
    {
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(seed);
        }
        return sb.ToString(0, length);
    }

    [Fact]
    public async Task HashEmbedder_512字符前后块交换_不同文本_向量可区分()
    {
        // 【修复反转】位置分桶 + FNV 内容哈希折叠:半块交换的对抗文本不再同向量
        var halfA = Padded("张三的高血压联合用药记录请复核。", 256);
        var halfB = Padded("李四的糖尿病随访计划需要调整。", 256);
        var text1 = halfA + halfB;
        var text2 = halfB + halfA; // 真实不同的两段文本(语序完全不同)
        Assert.NotEqual(text1, text2);

        var embedder = new HashEmbedder();
        var v1 = await embedder.EmbedAsync(text1);
        var v2 = await embedder.EmbedAsync(text2);

        Assert.NotEqual(v1, v2); // 修复:内容哈希折叠,任意不同文本向量不同(修复前逐位相同)
        var cos = InMemoryCosine(v1, v2);
        Assert.True(cos < 1.0, $"cos {cos:R} 不再精确为 1(修复前逐位相同→1.0)");
        // 诚实边界:同字符多重集文本(半块交换)在频率嵌入器下仍高相似(cos≈0.99)——
        // 频率嵌入器为开发/同题去重定位,生产语义缓存应配置真实嵌入模型(ToolSearch/OpenAI 兼容)
        Assert.True(cos >= 0.85, $"cos {cos:R}:多重集相同文本的类内相似残留(文档化局限)");
    }

    [Fact]
    public async Task HashEmbedder_分桶求和对抗_内容哈希使向量可区分()
    {
        // 【修复反转】分桶和相等对抗样本(bin0: 97+99==98+98)→ 内容哈希字节不同 → 向量不同
        var chars1 = new string('a', 512).ToCharArray();
        var chars2 = new string('a', 512).ToCharArray();
        chars1[256] = 'c';
        chars2[0] = 'b';
        chars2[256] = 'b';
        var text1 = new string(chars1);
        var text2 = new string(chars2);
        Assert.NotEqual(text1, text2);

        var embedder = new HashEmbedder();
        var v1 = await embedder.EmbedAsync(text1);
        var v2 = await embedder.EmbedAsync(text2);

        Assert.NotEqual(v1, v2); // 修复:内容哈希字节不同 → 向量不同(修复前逐位相同)
        Assert.True(InMemoryCosine(v1, v2) < 1.0); // 不再精确相同
    }

    [Fact]
    public async Task HashEmbedder_短文本对照组_无位置对齐的无关文本_向量可区分()
    {
        // 长度 ≤ 256 时每桶只含一个位置,不构成碰撞——对照组证明碰撞边界在 >256 字符区间。
        // 注意 HashEmbedder 是"位置 i%256 分桶"而非字符频次:同前缀中文短文本仍会高度相似(见 ToolSearch 倒挂实证),
        // 故对照组用位置与字符均不重叠的无关文本。
        var embedder = new HashEmbedder();
        var v1 = await embedder.EmbedAsync("怎么修改密码");
        var v2 = await embedder.EmbedAsync("hello world 123");

        Assert.NotEqual(v1, v2); // 修复:内容哈希字节不同 → 向量不同(修复前逐位相同)
        Assert.True(InMemoryCosine(v1, v2) < 1.0); // 不再精确相同
    }

    [Fact]
    public async Task 缓存错配实证_对抗样本_第二问命中第一问的答案()
    {
        // 缺陷钉死:两段真实不同的长问题,HashEmbedder 判为同题 → 第二问拿到第一问的缓存答案,模型零调用。
        // 修复 HashEmbedder/缓存后本断言应反转(Calls == 2 且答案不同)。
        var halfA = Padded("张三的高血压联合用药记录请复核。", 256);
        var halfB = Padded("李四的糖尿病随访计划需要调整。", 256);
        var inner = new CountingClient { Answer = "第一问的答案" };
        using var cache = Build(inner, threshold: 0.85);

        var first = await cache.GetResponseAsync(Ask(halfA + halfB));
        inner.Answer = "第二问的答案";
        var second = await cache.GetResponseAsync(Ask(halfB + halfA));

        Assert.Equal(1, inner.Calls); // 实证:模型只被调用一次 → 第二问被误判为同题
        Assert.Equal("第一问的答案", second.Text); // 缓存错配:第二问拿到第一问的答案
        Assert.Equal(first.Text, second.Text);
    }

    // ── ② 匿名上下文跨租户泄漏 ──────────────────────────────────────

    [Fact]
    public async Task 匿名上下文_不命中租户缓存条目_跨租户泄漏已封死()
    {
        // 【修复反转】存储键以 scope+"|" 为命名空间硬隔离:匿名("")只匹配匿名条目,
        // 租户答案对匿名不可见(修复前实证=匿名零调用拿到租户 PHI 答案)。
        var inner = new CountingClient { Answer = "hosp-A 的 PHI 答案" };
        using var cache = Build(inner, threshold: 0.85);

        string tenantAnswer;
        using (TenantHelper.Enter("hosp-A"))
        {
            tenantAnswer = (await cache.GetResponseAsync(Ask("患者床号3的用药方案"))).Text;
        }
        Assert.Equal(1, inner.Calls);
        Assert.Equal("hosp-A 的 PHI 答案", tenantAnswer);

        inner.Answer = "匿名侧模型答案";
        var anonymous = await cache.GetResponseAsync(Ask("患者床号3的用药方案")); // 不在 Enter 作用域内

        Assert.Equal(2, inner.Calls); // 修复后:匿名不读租户条目 → 模型真实调用
        Assert.Equal("匿名侧模型答案", anonymous.Text);
    }

    [Fact]
    public async Task 反向对照_租户上下文_不命中匿名写入的条目()
    {
        var inner = new CountingClient { Answer = "全局答案" };
        using var cache = Build(inner, threshold: 0.85);

        var anonymous = await cache.GetResponseAsync(Ask("怎么修改密码")); // 匿名写入 key 无租户前缀

        inner.Answer = "hosp-B 的答案";
        string tenantAnswer;
        using (TenantHelper.Enter("hosp-B"))
        {
            tenantAnswer = (await cache.GetResponseAsync(Ask("怎么修改密码"))).Text;
        }

        Assert.Equal("全局答案", anonymous.Text);
        Assert.Equal("hosp-B 的答案", tenantAnswer); // 非空 scope 过滤匿名条目:不反向泄漏
        Assert.Equal(2, inner.Calls);
    }

    // ── ③ 阈值恰好等于相似度的边界 ──────────────────────────────────

    [Fact]
    public async Task 相似度恰好等于阈值_命中_仅高一位不命中_存储级()
    {
        var store = new InMemorySemanticStore();
        float[] query = [1f, 0f];
        float[] stored = [0.5f, 0.5f]; // 余弦 = 0.5/√0.5(与存储实现同序运算,位级确定)
        await store.AddAsync("t1:", "q:1", stored, "P1");

        var boundary = ReplicaCosine(query, stored);
        var hit = await store.SearchAsync("t1:", query, boundary);
        var miss = await store.SearchAsync("t1:", query, Math.BitIncrement(boundary));

        Assert.NotNull(hit); // >= 语义:恰好等于阈值即命中
        Assert.Equal("P1", hit!.Payload);
        Assert.Equal(boundary, hit.Similarity);
        Assert.Null(miss); // 阈值抬高一个 ULP → 不命中
    }

    [Fact]
    public async Task 相似度恰好等于阈值_命中_缓存级()
    {
        float[] vectorA = [1f, 0f];
        float[] vectorB = [0.5f, 0.5f];
        var boundary = ReplicaCosine(vectorA, vectorB);
        var inner = new CountingClient { Answer = "回源答案" };
        using var cache = Build(
            inner,
            embedder: new PairEmbedder("Q1", vectorA, "Q2", vectorB),
            threshold: boundary);

        await cache.GetResponseAsync(Ask("Q1"));
        var second = await cache.GetResponseAsync(Ask("Q2")); // 相似度恰等于阈值 → 命中

        Assert.Equal(1, inner.Calls);
        Assert.Equal("回源答案", second.Text);
    }

    [Fact]
    public async Task 阈值仅高一个ULP_未命中_穿透回源_缓存级()
    {
        float[] vectorA = [1f, 0f];
        float[] vectorB = [0.5f, 0.5f];
        var boundary = Math.BitIncrement(ReplicaCosine(vectorA, vectorB));
        var inner = new CountingClient { Answer = "第一次答案" };
        using var cache = Build(
            inner,
            embedder: new PairEmbedder("Q1", vectorA, "Q2", vectorB),
            threshold: boundary);

        var first = await cache.GetResponseAsync(Ask("Q1"));
        inner.Answer = "第二次答案";
        var second = await cache.GetResponseAsync(Ask("Q2"));

        Assert.Equal(2, inner.Calls); // 差一个 ULP 即不命中,边界精确
        Assert.Equal("第二次答案", second.Text); // 未命中 → 重新回源
    }

    // ── ④ 并发写 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 并发写同一key_无异常无撕裂_终值必为已写payload()
    {
        var store = new InMemorySemanticStore();
        var payloads = Enumerable.Range(0, 32).Select(i => $"payload-{i}").ToHashSet();
        var vectors = Enumerable.Range(0, 32)
            .Select(i =>
            {
                var v = new float[256];
                v[i] = 1;
                return v;
            })
            .ToList();

        var errors = new List<Exception>();
        var searches = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var round = 0; round < 50; round++)
                {
                    var match = await store.SearchAsync("t1:", vectors[round % 32], 0.5);
                    if (match is not null)
                    {
                        // 撕裂读取检测:命中 payload 必属于已写集合(不属于则 Assert.Contains 抛出并计入 errors)
                        Assert.Contains(match.Payload, payloads);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        })).ToList();

        var writes = Enumerable.Range(0, 32).Select(w => Task.Run(async () =>
        {
            try
            {
                for (var round = 0; round < 50; round++)
                {
                    await store.AddAsync("t1:", "k", vectors[w], $"payload-{w}");
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        })).ToList();

        await Task.WhenAll(writes.Concat(searches));

        Assert.Empty(errors); // 并发 upsert/枚举无异常
        // 终态:32 个向量互相正交,任一向量检索都应命中唯一幸存条目,payload 必属于已写集合
        for (var w = 0; w < 32; w++)
        {
            var match = await store.SearchAsync("t1:", vectors[w], 0.5);
            if (match is not null)
            {
                Assert.Contains(match.Payload, payloads);
            }
        }
    }

    [Fact]
    public async Task 并发写不同key_并发检索_每向量命中正确payload()
    {
        var store = new InMemorySemanticStore();
        var vectors = Enumerable.Range(0, 32)
            .Select(i =>
            {
                var v = new float[256];
                v[i] = 1;
                return v;
            })
            .ToList();

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => store.AddAsync("t1:", $"q:{i}", vectors[i], $"payload-{i}")));

        // 全部写完后:正交向量检索必须精确命中各自条目
        for (var i = 0; i < 32; i++)
        {
            var match = await store.SearchAsync("t1:", vectors[i], 0.5);
            Assert.NotNull(match);
            Assert.Equal($"payload-{i}", match!.Payload);
        }
    }

    /// <summary>测试内复刻存储实现的余弦(内部实现无 InternalsVisibleTo)。</summary>
    private static double InMemoryCosine(float[] a, float[] b) => ReplicaCosine(a, b);
}
