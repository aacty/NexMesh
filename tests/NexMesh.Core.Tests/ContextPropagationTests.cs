namespace NexMesh.Core.Tests;

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.Core.Context;

public class ContextPropagationTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task 离线任务_显式绑定后读到身份与租户()
    {
        var provider = BuildProvider();
        var runner = provider.GetRequiredService<INexMeshBackgroundRunner>();

        var snapshot = new NexMeshContext
        {
            UserId = "u-1001",
            UserName = "Dr.张三",
            TenantId = "hosp-A",
            Claims = new Dictionary<string, string> { ["role"] = "admin" },
        };

        string? userId = null, userName = null, tenantId = null, role = null;
        await runner.RunAsync(snapshot, async (sp, _) =>
        {
            var user = sp.GetRequiredService<ICurrentUser>();
            userId = user.UserId;
            userName = user.UserName;
            tenantId = user.TenantId;
            role = user.FindClaim("role")?.Value;
            await Task.CompletedTask;
        });

        Assert.Equal("u-1001", userId);
        Assert.Equal("Dr.张三", userName);
        Assert.Equal("hosp-A", tenantId);
        Assert.Equal("admin", role);
    }

    [Fact]
    public async Task 绑定作用域_任务结束自动还原()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<INexMeshContextAccessor>();
        var runner = provider.GetRequiredService<INexMeshBackgroundRunner>();

        await runner.RunAsync(new NexMeshContext { UserId = "temp" }, async (_, _) => await Task.CompletedTask);

        Assert.Null(accessor.Current); // 任务结束,环境槽还原为先前值(null)
    }

    [Fact]
    public async Task 嵌套绑定_内层还原不破坏外层()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<INexMeshContextAccessor>();

        using (accessor.Enter(new NexMeshContext { UserId = "outer" }))
        {
            using (accessor.Enter(new NexMeshContext { UserId = "inner" }))
            {
                Assert.Equal("inner", accessor.Current?.UserId);
            }
            Assert.Equal("outer", accessor.Current?.UserId);
        }
        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task 并行任务_快照互不污染()
    {
        var accessor = new AsyncLocalNexMeshContextAccessor();

        // 20 个并发异步流各自绑定不同身份(模拟张三/李四并行请求),线程池复用下互不可见
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            await Task.Yield();
            using (accessor.Enter(new NexMeshContext { UserId = $"u-{i}", TenantId = $"hosp-{i}" }))
            {
                for (var round = 0; round < 5; round++)
                {
                    await Task.Delay(Random.Shared.Next(1, 5)); // 触发线程池调度与复用
                    Assert.Equal($"u-{i}", accessor.Current?.UserId);
                    Assert.Equal($"hosp-{i}", accessor.Current?.TenantId);
                }
            }
        });

        await Task.WhenAll(tasks); // 全部断言通过 = 无上下文穿帮
    }

    [Fact]
    public async Task 消息头序列化_身份租户声明与链路往返无损()
    {
        var traceId = ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c");
        var spanId = ActivitySpanId.CreateFromString("00f067aa0ba902b7");
        var original = new NexMeshContext
        {
            UserId = "u-9",
            TenantId = "hosp-B",
            Claims = new Dictionary<string, string> { ["role"] = "nurse" },
            TraceContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded),
        };

        var restored = NexMeshContext.FromMessageHeaders(original.ToMessageHeaders());

        Assert.NotNull(restored);
        Assert.Equal("u-9", restored.UserId);
        Assert.Equal("hosp-B", restored.TenantId);
        Assert.Equal("nurse", restored.Claims["role"]);
        Assert.Equal(traceId, restored.TraceContext.TraceId);
        Assert.Equal(ActivityTraceFlags.Recorded, restored.TraceContext.TraceFlags);
    }

    [Fact]
    public async Task 双源读取_环境槽优先于HttpContext()
    {
        var ambient = new AsyncLocalNexMeshContextAccessor();
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "from-http"),
                    new Claim("tenantid", "hosp-http"),
                ], "Test")),
            },
        };
        var user = new CurrentUser(ambient, http);

        Assert.Equal("from-http", user.UserId);          // 无环境槽 → HttpContext 兜底

        using (ambient.Enter(new NexMeshContext { UserId = "from-ambient", TenantId = "hosp-bg" }))
        {
            Assert.Equal("from-ambient", user.UserId);   // 有环境槽 → 优先
            Assert.Equal("hosp-bg", user.TenantId);
        }
        Assert.Equal("from-http", user.UserId);          // 还原后回退 HTTP 源
    }
}
