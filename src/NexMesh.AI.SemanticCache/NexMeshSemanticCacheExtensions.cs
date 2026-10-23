using NexMesh.AI.Tools;
namespace NexMesh.AI.SemanticCache;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Core;
using NexMesh.Cache;

/// <summary>SemanticCache 注册与管道接入。</summary>
public static class NexMeshSemanticCacheExtensions
{
    /// <summary>
    /// 注册语义缓存(NexMesh:AI:SemanticCache 配置节):
    /// Enabled=true 时工厂管道自动挂载装饰器(内层:弹性→语义缓存→裸模型——命中在弹性之前,零模型调用);
    /// Store=Memory(默认,单实例)|Redis(Redis Stack FT.SEARCH,复用 NexMesh:Cache:Sources 的连接);
    /// 嵌入:容器已有 ISemanticEmbedder 用之(业务自接 openai-compatible embedding);无则 HashEmbedder 兜底(仅开发)。
    /// </summary>
    public static IServiceCollection AddNexMeshSemanticCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new SemanticCacheOptions();
        configuration.GetSection("NexMesh:AI:SemanticCache").Bind(options);
        if (!options.Enabled)
        {
            return services; // 未启用:零注册零依赖
        }

        // 存储:Memory(默认)|Redis(复用 Cache 的 SER 连接)
        if (options.Store.Equals("Redis", StringComparison.OrdinalIgnoreCase))
        {
            var cacheConnection = configuration["NexMesh:Cache:Sources:main:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(cacheConnection))
            {
                var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(cacheConnection);
                services.AddSingleton<ISemanticVectorStore>(new RedisSemanticStore(redis, options.KeyPrefix));
            }
            else
            {
                // 配了 Redis 但没有连接串 → 内存兜底(不 fail,日志警告)
                services.AddSingleton<ISemanticVectorStore>(new InMemorySemanticStore());
            }
        }
        else
        {
            services.AddSingleton<ISemanticVectorStore>(new InMemorySemanticStore());
        }

        // 嵌入:业务已注册 ISemanticEmbedder 用之;否则 HashEmbedder 兜底(开发/测试,无真实语义)
        services.TryAddSingleton<ISemanticEmbedder, HashEmbedder>();
        services.AddSingleton(options);
        services.AddSingleton<ISemanticCacheWrapper, SemanticCacheWrapper>();
        return services;
    }
}

/// <summary>
/// AI 核心的接缝实现:工厂管道自动挂载(命中→零模型调用)。
/// </summary>
public sealed class SemanticCacheWrapper(
    ISemanticEmbedder embedder,
    ISemanticVectorStore store,
    SemanticCacheOptions options,
    ILogger<SemanticCacheChatClient>? logger = null) : ISemanticCacheWrapper
{
    public IChatClient Wrap(IChatClient inner) =>
        new SemanticCacheChatClient(inner, embedder, store, options, logger);
}


