namespace NexMesh.AI.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexMesh.AI.Configuration;

/// <summary>Chat 工厂:按 Provider 名称取预构建的 IChatClient(含日志/遥测/函数调用管道)。</summary>
public interface INexMeshChatFactory
{
    IChatClient CreateClient(string? providerName = null);
}

/// <summary>
/// Singleton 工厂:构造时一次性从 DI 解析全部 Provider 的 IChatClient(经 AddKeyedChatClient
/// 预构建的单例管道)缓存字典,CreateClient 仅做字典查找——请求路径零构建、零连接池重建。
/// </summary>
public class NexMeshChatFactory : INexMeshChatFactory
{
    private readonly Dictionary<string, IChatClient> _clientCache;
    private readonly string _defaultProvider;

    public NexMeshChatFactory(IOptions<NexMeshAiOptions> options, IServiceProvider services)
    {
        var opts = options.Value;
        _defaultProvider = opts.DefaultProvider;
        _clientCache = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in opts.Providers.Keys)
        {
            _clientCache[name] = services.GetRequiredKeyedService<IChatClient>(name);
        }
    }

    public IChatClient CreateClient(string? providerName = null)
    {
        providerName ??= _defaultProvider;
        if (!_clientCache.TryGetValue(providerName, out var client))
        {
            throw new InvalidOperationException($"LLM Provider '{providerName}' is not registered or configured.");
        }
        return client;
    }
}
