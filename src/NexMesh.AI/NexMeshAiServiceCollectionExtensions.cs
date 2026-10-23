namespace NexMesh.AI;

using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexMesh.AI.Configuration;
using NexMesh.AI.Core;
using NexMesh.AI.Tools;
using OpenAI;
using System.ClientModel;

/// <summary>NexMesh.AI 注册入口。</summary>
public static class NexMeshAiServiceCollectionExtensions
{
    /// <summary>
    /// 注册 AI 底座:
    /// 1) 配置绑定(NexMesh:AI);
    /// 2) 每个 Provider 经 AddKeyedChatClient 注册为 DI 单例管道(日志/遥测/函数调用);
    /// 3) Chat 工厂(Singleton,构造时预构建全部 Provider 字典缓存);
    /// 4) 工具注册器——注册期即执行一次工具发现,把安全铁律(High 风险阻断/实例方法拒绝)
    ///    落实为启动期失败(fail-fast)。
    /// </summary>
    public static IServiceCollection AddNexMeshAi(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        services.AddOptions<NexMeshAiOptions>()
            .Configure(o => configuration.GetSection(NexMeshAiOptions.SectionName).Bind(o));

        // 实例工具宿主类自动注册为 Scoped(未显式注册时),使每次调用的 DI 解析可用
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
                     .Where(t => t is { IsClass: true, IsAbstract: false }
                         && t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                             .Any(m => m.GetCustomAttribute<NexMeshToolAttribute>() != null)))
        {
            if (!services.Any(d => d.ServiceType == type))
            {
                services.AddScoped(type);
            }
        }

        var options = configuration.GetSection(NexMeshAiOptions.SectionName).Get<NexMeshAiOptions>() ?? new NexMeshAiOptions();
        foreach (var (name, config) in options.Providers)
        {
            // 启动期配置校验(fail-fast):非法 Provider 配置不允许等到第一次调用才暴露
            if (string.IsNullOrWhiteSpace(config.Model))
            {
                throw new InvalidOperationException(
                    $"[NexMesh AI] Provider '{name}' 缺少 Model 配置(NexMesh:AI:Providers:{name}:Model)。");
            }
            if (!string.IsNullOrEmpty(config.Endpoint)
                && !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"[NexMesh AI] Provider '{name}' 的 Endpoint 不是合法的绝对 URI:{config.Endpoint}");
            }

            if (config.Type.Equals("stub", StringComparison.OrdinalIgnoreCase))
            {
                // stub:离线开发/演示,无管道装饰
                services.AddKeyedChatClient(name, _ => new Core.StubChatClient(config.Model));
                continue;
            }

            var resilience = ReadResilienceOptions(configuration);
            // ToolSearch 语义召回(配置 NexMesh:AI:ToolSearch:Enabled=true 启用;替代 MaxToolsPerRequest 硬截断)
            var toolSearch = ReadToolSearchOptions(configuration);
            var builder = services.AddKeyedChatClient(name, sp => BuildInnerClient(config))
                .Use((inner, sp) => new Core.NexMeshCancellationGuardChatClient(
                    inner, sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>())) // 断连熔断:最外层,链接 RequestAborted
                .UseLogging()
                .UseOpenTelemetry()
                .UseFunctionInvocation();
            if (toolSearch.Enabled)
            {
                // 位于 FunctionInvocation 之外层:模型调用前改写 options.Tools(Top-K 语义召回)
                // 嵌入实现兜底:业务自接真实 embedder 优先,否则 HashEmbedder(开发)
                services.TryAddSingleton<Tools.ISemanticEmbedder, Tools.HashEmbedder>();
                builder = builder.Use((inner, sp) => new Tools.ToolSearchChatClient(
                    inner,
                    sp.GetRequiredService<Tools.ISemanticEmbedder>(),
                    sp.GetRequiredService<INexMeshToolRegistry>(),
                    toolSearch.TopK));
            }
            if (resilience.Enabled)
            {
                // 弹性管道:最内层,只包裹裸模型调用(重试/熔断/超时不波及工具与多轮循环)
                builder = builder.Use((inner, _) => new Resilience.ResilientChatClient(inner, resilience));
            }
            // 语义缓存(接口反转:AI 核心定义 ISemanticCacheWrapper,SemanticCache 包实现;启用时命中→零模型调用)
            builder = builder.Use((inner, sp) => sp.GetService<ISemanticCacheWrapper>()?.Wrap(inner) ?? inner);
            _ = builder; // ChatClientBuilder 由 DI 容器隐式 Build
        }

        services.AddSingleton<INexMeshChatFactory, NexMeshChatFactory>();
        services.AddSingleton<INexMeshToolRegistry>(sp => new NexMeshToolRegistry(configuration, sp, assemblies));

        // 启动期安全门验证:发现期即抛(验证模式,不做实例绑定)→ 服务注册阶段失败 → 应用拒绝启动
        _ = new NexMeshToolRegistry(configuration, services: null, assemblies).DiscoverTools().Count();
        return services;
    }

    /// <summary>构建 openai-compatible 底层客户端(覆盖 OpenAI/DeepSeek/Ollama,仅 Endpoint/ApiKey/Model 不同)。</summary>
    internal static IChatClient BuildInnerClient(Configuration.LlmProviderConfig config)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(config.Endpoint))
        {
            clientOptions.Endpoint = new Uri(config.Endpoint);
        }

        // 本地 openai-compatible 端点(如 Ollama)可能无 Key,给占位凭据避免构造失败
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(config.ApiKey) ? "nexmesh-empty" : config.ApiKey);
        var openAiClient = new OpenAIClient(credential, clientOptions);
        return openAiClient.GetChatClient(config.Model).AsIChatClient();
    }

    private static Resilience.ResilienceOptions ReadResilienceOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("NexMesh:AI:Resilience");
        var options = new Resilience.ResilienceOptions();
        if (section.Exists())
        {
            section.Bind(options);
        }
        return options;
    }

    private static ToolSearchOptions ReadToolSearchOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("NexMesh:AI:ToolSearch");
        var options = new ToolSearchOptions();
        if (section.Exists())
        {
            section.Bind(options);
        }
        if (options.TopK < 1)
        {
            throw new InvalidOperationException("[NexMesh AI] NexMesh:AI:ToolSearch:TopK 必须 ≥ 1。");
        }
        return options;
    }
}

/// <summary>
/// 语义缓存接缝(依赖方向:AI 核心定义,SemanticCache 包实现;未启用时容器无注册→装饰器不挂载)。
/// </summary>
public interface ISemanticCacheWrapper
{
    IChatClient Wrap(IChatClient inner);
}

/// <summary>ToolSearch 语义召回配置(NexMesh:AI:ToolSearch)。</summary>
public sealed class ToolSearchOptions
{
    public bool Enabled { get; set; } = false;
    public int TopK { get; set; } = 5;
}
