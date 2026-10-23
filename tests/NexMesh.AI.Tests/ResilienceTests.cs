namespace NexMesh.AI.Tests;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using NexMesh.AI.Core;
using NexMesh.AI.Tests.Fixtures;
using NexMesh.AiFixtures.Instance;
using NexMesh.AI.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>三层隐藏灾难防御的回归测试:断连熔断 / 工具自纠错 / (并行隔离见 Core.Tests)。</summary>
public class ResilienceTests
{
    // ── 防御一:大模型吸血鬼效应 ─────────────────────────────────

    /// <summary>尊重取消令牌的假模型客户端(模拟持续产出 token 的流)。</summary>
    private sealed class SlowLlmClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await Task.Delay(10, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "token");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RequestLifetime(CancellationToken aborted) : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = aborted;
        public void Abort() { }
    }

    [Fact]
    public async Task 请求中断_出站LLM流式调用立即熔断()
    {
        using var requestCts = new CancellationTokenSource();
        var http = new DefaultHttpContext();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(
            new RequestLifetime(requestCts.Token));
        var accessor = new HttpContextAccessor { HttpContext = http };

        var guard = new NexMeshCancellationGuardChatClient(new SlowLlmClient(), accessor);

        var streaming = guard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        var enumerator = streaming.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync()); // 第一段 token 正常到达

        requestCts.Cancel(); // 用户关闭浏览器
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task 请求外_守卫退化为透传不额外取消()
    {
        using var guard = new NexMeshCancellationGuardChatClient(new SlowLlmClient(), httpAccessor: null);
        using var callerCts = new CancellationTokenSource(50); // 离线调用方自身的超时令牌

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: callerCts.Token));
    }

    // ── 防御三:大模型输出与强类型契约错配的自纠错 ──────────────────

    private static INexMeshToolRegistry BuildRegistry(System.Reflection.Assembly assembly, int maxTools = 100)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:MaxToolsPerRequest"] = maxTools.ToString(),
        }).Build();
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        return new NexMeshToolRegistry(config, sp, assembly);
    }

    private static AIFunction FindTool(INexMeshToolRegistry registry, string name) =>
        registry.DiscoverTools().First(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task 静态工具_模型日期写错_不崩溃并回喂纠错提示()
    {
        var tool = FindTool(BuildRegistry(typeof(LowTools).Assembly), "Restock");
        var badDate = JsonSerializer.SerializeToElement("二〇二六年十二月三十一日");

        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["effectiveDate"] = badDate,
            ["quantity"] = 5,
        });

        var text = Assert.IsType<string>(result);
        Assert.Contains("[NexMesh Tool Error]", text);
        Assert.Contains("yyyy-MM-dd", text);
        Assert.Contains("重新调用", text);
    }

    [Fact]
    public async Task 实例工具_模型日期写错_同样自纠错()
    {
        var services = new ServiceCollection();
        services.AddScoped<IProbeDependency, ProbeDependency>();
        services.AddNexMeshAi(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            }).Build(),
            typeof(InstanceToolFixture).Assembly);
        using var sp = services.BuildServiceProvider();

        var tool = FindTool(sp.GetRequiredService<INexMeshToolRegistry>(), "ScheduleRounds");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["effectiveDate"] = JsonSerializer.SerializeToElement("明天吧"),
        });

        var text = Assert.IsType<string>(result);
        Assert.Contains("[NexMesh Tool Error]", text);
    }

    [Fact]
    public async Task 参数正确时_自纠错防线零侵入()
    {
        var tool = FindTool(BuildRegistry(typeof(LowTools).Assembly), "Restock");
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["effectiveDate"] = JsonSerializer.SerializeToElement("2026-12-31"),
            ["quantity"] = 5,
        });

        Assert.DoesNotContain("Tool Error", result?.ToString());
        Assert.Contains("2026-12-31", result?.ToString());
    }
}
