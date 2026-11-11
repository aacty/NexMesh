namespace NexMesh.Host.Tests;

using System.Text.Json;
using FreeSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.AI.Core;
using NexMesh.Auth;
using NexMesh.Data;
using NexMesh.Data.Migrations;
using NexMesh.Host;
using Xunit;

/// <summary>H 重复版本夹具 A/B:同 Source=main 同 Version(小白手滑复制粘贴迁移类的典型事故)。</summary>
public sealed class H_DupMigrationA : NexMeshMigration
{
    public override long Version => 20991002001;
    public override string Name => "重复版本演示A";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class H_DupMigrationB : NexMeshMigration
{
    public override long Version => 20991002001;
    public override string Name => "重复版本演示B";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>H 篡改检测夹具:同 Version/Name 不同类型全名 → Checksum 与历史记录不一致。</summary>
public sealed class H_TamperMigration : NexMeshMigration
{
    public override long Version => 20991002002;
    public override string Name => "篡改检测演示";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class H_TamperMigrationModified : NexMeshMigration
{
    // 独立类型但 Version/Name 与 H_TamperMigration 相同(手改 UpAsync 逻辑不改版本号的事故形态);
    // 类型全名不同 → Checksum(Version|Name|FullName)必然不同
    public override long Version => 20991002002;
    public override string Name => "篡改检测演示";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// H 第二轮交叉测试:配置错误 fail-fast 质量(异常消息能否让小白定位问题)。
/// 迁移重复版本/篡改检测:消息含 Source/Version/类名 → 定位质量好 ✓;
/// AI 缺 Model/非法 Endpoint:消息含配置键路径 → 好 ✓;
/// AI 未知 Type(拼写错):不 fail-fast,静默按 openai-compatible 处理 → 缺口备案 P2;
/// 限流策略名含冒号:配置节层级把策略名截断,注册名与端点标注名失配(运行期 500)→ 缺口备案 P2;
/// 登录限流 Store=Redis 但容器无 Redis:消息好但被 500 通用文案吞掉 → 缺口备案 P2。
/// </summary>
public class HConfigFailFastTests
{
    private static ServiceProvider BuildMigrationProvider(string dbName, params Type[] migrationTypes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = DataType.Sqlite;
            o.ConnectionString = $"Data Source={dbName}"; // 篡改检测需两次 Run 落同一库(读历史指纹)
            o.AutoSyncStructure = true;
        });
        foreach (var type in migrationTypes)
        {
            services.AddScoped(typeof(NexMeshMigration), type);
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task 同源同版本重复迁移_启动校验fail_fast_消息含源版本与类名可定位()
    {
        using var provider = BuildMigrationProvider(
            $"hfailfast-dup-{Guid.NewGuid():N}.db", typeof(H_DupMigrationA), typeof(H_DupMigrationB));
        var runner = new NexMeshMigrationRunner(provider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunPendingAsync());
        // 消息定位质量断言(对照"既有教训":小白复制迁移类改逻辑不改版本号的高频事故)
        Assert.Contains("存在重复版本号", ex.Message);
        Assert.Contains("Source=main Version=20991002001", ex.Message);
        Assert.Contains("H_DupMigrationA", ex.Message);
        Assert.Contains("H_DupMigrationB", ex.Message);
    }

    [Fact]
    public async Task 已发布迁移被改动_Checksum不一致_fail_fast_消息指引新增修正迁移()
    {
        var dbFile = $"hfailfast-{Guid.NewGuid():N}.db";
        using var provider1 = BuildMigrationProvider(dbFile, typeof(H_TamperMigration));
        var runner1 = new NexMeshMigrationRunner(provider1);
        var applied = await runner1.RunPendingAsync();
        Assert.Equal(1, applied); // 正常应用一次

        // "改动后"的迁移:同 Version 同 Name(手改 UpAsync 逻辑不改版本号的事故形态)
        using var provider2 = BuildMigrationProvider(dbFile, typeof(H_TamperMigrationModified));
        var runner2 = new NexMeshMigrationRunner(provider2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner2.RunPendingAsync());
        Assert.Contains("已被发布且被改动", ex.Message);
        Assert.Contains("Checksum 不一致", ex.Message);
        Assert.Contains("请新增一条迁移修正", ex.Message);
    }

    [Fact]
    public void AI提供方缺Model_注册期fail_fast_消息含配置键路径()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Db:ConnectionString"] = $"Data Source=hai-{Guid.NewGuid():N}.db",
            ["NexMesh:AI:Providers:broken:Type"] = "stub",
            ["NexMesh:AI:Providers:broken:ApiKey"] = "k",
            // 故意缺 Model
        });
        builder.WebHost.UseTestServer();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddNexMeshService(typeof(HConfigFailFastTests).Assembly));
        // 消息定位质量断言:含配置键完整路径(小白可直接到 appsettings 修正)
        Assert.Contains("[NexMesh AI]", ex.Message);
        Assert.Contains("Provider 'broken' 缺少 Model", ex.Message);
        Assert.Contains("NexMesh:AI:Providers:broken:Model", ex.Message);
    }

    [Fact]
    public void AI提供方Endpoint非法URI_注册期fail_fast_消息含原始值()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Db:ConnectionString"] = $"Data Source=hai2-{Guid.NewGuid():N}.db",
            ["NexMesh:AI:Providers:badurl:Type"] = "openai-compatible",
            ["NexMesh:AI:Providers:badurl:Endpoint"] = "not-a-valid-uri",
            ["NexMesh:AI:Providers:badurl:Model"] = "m",
        });
        builder.WebHost.UseTestServer();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddNexMeshService(typeof(HConfigFailFastTests).Assembly));
        Assert.Contains("badurl", ex.Message);
        Assert.Contains("Endpoint 不是合法的绝对 URI", ex.Message);
        Assert.Contains("not-a-valid-uri", ex.Message);
    }

    [Fact]
    public async Task AI提供方Type拼写错_启动即FailFast_消息含配置键路径()
    {
        // 【修复反转】未知 Type 启动即抛(此前静默落 openai-compatible 分支,首次调用才暴露)
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Db:ConnectionString"] = $"Data Source=hai3-{Guid.NewGuid():N}.db",
            ["NexMesh:AI:Providers:weird:Type"] = "bogus", // 拼写错(合法值仅 stub/openai-compatible)
            ["NexMesh:AI:Providers:weird:Model"] = "m",
        });
        builder.WebHost.UseTestServer();

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddNexMeshService(typeof(HConfigFailFastTests).Assembly));
        Assert.Contains("'bogus'", ex.Message);
        Assert.Contains("NexMesh:AI:Providers:weird:Type", ex.Message);
        Assert.Contains("stub / openai-compatible", ex.Message);
    }
    [Fact]
    public void 限流策略名含冒号_配置节层级截断策略名_注册名与标注名失配备案()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // 运维把策略名写成 "api:write"(冒号是配置路径分隔符,嵌进了节层级)
            ["NexMesh:RateLimit:Policies:api:write:PermitLimit"] = "5",
            ["NexMesh:RateLimit:Policies:api:write:WindowSeconds"] = "60",
        }).Build();

        var services = new ServiceCollection();
        services.AddNexMeshRateLimiter(configuration);
        using var provider = services.BuildServiceProvider();
        var marker = provider.GetRequiredService<NexMeshRateLimitMarker>();

        // 缺口备案(P2):策略名 "api:write" 被配置层级拆解,实际注册键为 "api"(冒号丢失)——
        // 端点标注 [EnableRateLimiting("api:write")] 与注册名失配;启动期仅告警不拦截,
        // 该端点运行期首请求即 500(RateLimitPolicyAudit 的注释自证)。含冒号策略名应 fail-fast 拒绝注册。
        Assert.DoesNotContain("api:write", marker.RegisteredPolicies);
        Assert.Contains("api", marker.RegisteredPolicies);
    }

    [Fact]
    public async Task 登录限流Store为Redis但容器无Redis_首登500_可定位消息被通用文案吞掉备案()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Db:ConnectionString"] = $"Data Source=hredis-{Guid.NewGuid():N}.db",
            ["NexMesh:Auth:LoginRateLimit:Store"] = "Redis", // 多实例运维照抄模板,但本地/测试容器没装 Redis
        });
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(HConfigFailFastTests).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        var app = builder.Build();
        app.UseNexMeshService(typeof(HConfigFailFastTests).Assembly);
        app.MapNexMeshAuth();
        await app.StartAsync();
        var client = app.GetTestClient();

        // 缺口备案(P2):异常消息本身质量好(指明补 AddNexMeshRedisAuthStore 或配 Redis 连接串),
        // 但经全局异常中间件 500 兜底后被 "服务器内部错误(TraceId:…)" 吞掉;
        // 且这是 lazy 解析——首次登录才炸,不是启动期 fail-fast
        var response = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "a", password = "b" }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AddNexMeshRedisAuthStore", raw); // 定位消息未透出
        await app.DisposeAsync();
    }
}
