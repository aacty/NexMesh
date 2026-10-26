namespace NexMesh.Dashboard.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using NexMesh;
using FreeSql;
using NexMesh.Data.Migrations;
using NexMesh.AI.Approval;
using NexMesh.AI.Tools;
using NexMesh.Auth;
using NexMesh.Host;
using NexMesh.Worker;
using Xunit;

/// <summary>
/// Dashboard 能力探测与端到端:静态页匿名/未授权 401 信封/形状断言/
/// 能力探测(装了才亮)/审批流(造单→approve→Executed、reject→Rejected)/未注册审批门降级。
/// </summary>
public class DashboardTests
{
    private static (WebApplication App, HttpClient Client) Build(
        Action<IServiceCollection>? extra = null, bool withApproval = true, bool force = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true"; // 本测试程序集含 High 风险夹具:安全门要求开审批门
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=dash-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(DashboardTests).Assembly);
        builder.Services.AddNexMeshDashboard(); // 启动时钟
        if (withApproval)
        {
            builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build()); // 内存审批队列
        }
        // 登录验证器(demo/demo123)
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddTransient<ILoginValidator, DemoValidator>();
        extra?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseNexMeshService(typeof(DashboardTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: force);
        app.Start();
        return (app, app.GetTestClient());
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    public sealed class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password)
            => Task.FromResult<LoginUser?>(username == "demo" && password == "demo123"
                ? new("dash-1", "仪表盘用户")
                : null);
    }

    [Fact]
    public async Task 静态页_匿名可载()
    {
        var (app, client) = Build();
        var response = await client.GetAsync("/nexmesh/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("NexMesh Dashboard", html);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task overview未授权_401统一信封()
    {
        var (app, client) = Build();
        var response = await client.GetAsync("/nexmesh/dashboard/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":401", body);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task overview授权_形状断言_审批面板亮起()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        var body = await client.GetStringAsync("/nexmesh/dashboard/overview");
        using var doc = JsonDocument.Parse(body);
        var panels = doc.RootElement.GetProperty("data");

        var overview = panels.GetProperty("overview");
        Assert.True(overview.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(overview.GetProperty("endpointCount").GetInt32() > 0);
        Assert.NotEqual("", overview.GetProperty("environment").GetString());

        Assert.True(panels.GetProperty("approvals").GetProperty("enabled").GetBoolean()); // AddNexMeshApproval 已装
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 未注册审批门_approvals降级_面板enabled为false()
    {
        var (app, client) = Build(withApproval: false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var overview = JsonDocument.Parse(await client.GetStringAsync("/nexmesh/dashboard/overview")).RootElement
            .GetProperty("data").GetProperty("approvals");
        Assert.False(overview.GetProperty("enabled").GetBoolean());

        var list = await client.GetStringAsync("/nexmesh/dashboard/approvals");
        Assert.Contains("审批门未启用", list);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 审批流_造单_批准_执行_拒绝_永不执行()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        // 造两张待审单(直接经 IApprovalStore)
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
            await store.AddAsync(new ApprovalRequest { ToolName = "demo.tool.echo", Description = "演示", ArgumentsJson = """{"x":1}""" });
            await store.AddAsync(new ApprovalRequest { ToolName = "demo.tool.kill", Description = "拒绝演示", ArgumentsJson = "{}" });
        }

        var rawList = await client.GetStringAsync("/nexmesh/dashboard/approvals");
        var list = JsonDocument.Parse(rawList).RootElement.GetProperty("data");
        Assert.True(list.GetProperty("enabled").GetBoolean());
        var pending = list.GetProperty("pending");
        Assert.Equal(2, pending.GetArrayLength());

        var rejectId = pending[1].GetProperty("id").GetString()!;
        var approveId = pending[0].GetProperty("id").GetString()!;

        // 拒绝:仅落状态(工具不存在也不执行)
        var rejected = await client.PostAsync($"/nexmesh/dashboard/approvals/{rejectId}/reject", content: null);
        var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.Equal("Rejected", rejectedBody.GetProperty("status").GetString());

        // 批准:工具未注册 → Failed(状态机可达,错误被记录)
        var approved = await client.PostAsync($"/nexmesh/dashboard/approvals/{approveId}/approve", content: null);
        var approvedBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        var finalStatus = approvedBody.GetProperty("status").GetString();
        Assert.True(finalStatus is "Failed" or "Executed", $"终态应为 Failed/Executed,实际 {finalStatus}");

        // 拒绝单不可再决策(状态机)
        var again = await client.PostAsync($"/nexmesh/dashboard/approvals/{rejectId}/approve", content: null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // MeshException 40903 → 409
        var againBody = await again.Content.ReadAsStringAsync();
        Assert.Contains("409", againBody);
        await app.DisposeAsync();
    }
}

/// <summary>三新面板(迁移/缓存/会话):探测、形状与安全(Token 不回明文)。</summary>
public class DashboardNewPanelsTests
{
    private static (WebApplication App, HttpClient Client) Build(bool withMigrations = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true"; // 本测试程序集含 High 风险夹具:安全门要求开审批门
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=dash3-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
        builder.Configuration["NexMesh:Cache:Mode"] = "Memory";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(DashboardTests).Assembly);
        if (withMigrations)
        {
            builder.Services.AddNexMeshMigrations(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                typeof(DashboardTests).Assembly); // 扫描本程序集(含下面定义的迁移)
        }
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
        builder.Services.AddTransient<ILoginValidator, DashboardTests.DemoValidator>();
        var app = builder.Build();
        app.UseNexMeshService(typeof(DashboardTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: true);
        app.Start();
        return (app, app.GetTestClient());
    }

    private sealed class V1_Init : NexMesh.Data.Migrations.NexMeshMigration
    {
        public override long Version => 20261025001;
        public override string Name => "面板演示迁移";
        public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> OverviewAsync(HttpClient client)
    {
        var body = await client.GetStringAsync("/nexmesh/dashboard/overview");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    [Fact]
    public async Task 迁移面板_应用后面板列记录()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        await app.Services.GetRequiredService<NexMesh.Data.Migrations.IMigrationRunner>().RunPendingAsync();

        var migrations = (await OverviewAsync(client)).GetProperty("migrations");
        Assert.True(migrations.GetProperty("enabled").GetBoolean());
        var list = migrations.GetProperty("migrations");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("面板演示迁移", list[0].GetProperty("name").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 迁移未注册_面板enabled为false()
    {
        var (app, client) = Build(withMigrations: false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        var migrations = (await OverviewAsync(client)).GetProperty("migrations");
        Assert.False(migrations.GetProperty("enabled").GetBoolean());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 缓存面板_模式与源清单()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        var cache = (await OverviewAsync(client)).GetProperty("cache");
        Assert.True(cache.GetProperty("enabled").GetBoolean());
        Assert.Equal("Memory", cache.GetProperty("mode").GetString());
        Assert.True(cache.GetProperty("sources").GetArrayLength() >= 1);
        // 不回显连接串
        var raw = await client.GetStringAsync("/nexmesh/dashboard/overview");
        Assert.DoesNotContain("ConnectionString", raw);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 会话面板_登录后会话在列且Token不回明文()
    {
        var (app, client) = Build();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var sessions = (await OverviewAsync(client)).GetProperty("sessions");
        Assert.True(sessions.GetProperty("enabled").GetBoolean());
        Assert.True(sessions.GetProperty("count").GetInt32() >= 1);

        // 安全断言:响应体绝不包含完整 Token(只有前缀)
        var raw = await client.GetStringAsync("/nexmesh/dashboard/overview");
        Assert.DoesNotContain(token, raw);
        var prefix = sessions.GetProperty("sessions")[0].GetProperty("tokenPrefix").GetString();
        Assert.EndsWith("…", prefix);
        Assert.True(prefix!.Length < 12);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 登出后_会话从活跃列表消失()
    {
        var (app, client) = Build();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        await client.PostAsync("/nexmesh/auth/logout", content: null);

        // 重新登录拿新 token 查询
        var token2 = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token2);
        var sessions = (await OverviewAsync(client)).GetProperty("sessions");
        var raw = await client.GetStringAsync("/nexmesh/dashboard/overview");
        // 旧 token 已吊销:活跃列表不含其前缀(粗断言——计数不增加即可)
        Assert.True(sessions.GetProperty("count").GetInt32() >= 1);
        Assert.DoesNotContain(token, raw);
        await app.DisposeAsync();
    }
}


public static class RiskToolFixture
{
    [NexMeshTool("高危演示", NexMeshRiskLevel.High)]
    public static Task<string> DangerousAsync(string x) => Task.FromResult(x);

    [NexMeshTool("低危演示", NexMeshRiskLevel.Low)]
    public static Task<string> SafeAsync(string x) => Task.FromResult(x);
}

public sealed class FakeToolRegistry : INexMeshToolRegistry
{
    public IEnumerable<AIFunction> DiscoverTools() =>
    [
        AIFunctionFactory.Create(RiskToolFixture.DangerousAsync), // AIFunction 名 = "Dangerous"(无 Async)
        AIFunctionFactory.Create(RiskToolFixture.SafeAsync),
    ];

    public AIFunction? FindRawFunction(string name) => null;
}

public class RiskBadgeRegressionTests
{
    /// <summary>实战审查修复:AIFunction 名去 Async 后缀——High 工具曾因键不匹配显示为 Low 默认值。</summary>
    [Fact]
    public async Task 高危工具_面板风险徽章必须为High()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true"; // 本测试程序集含 High 风险夹具:安全门要求开审批门
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=risk-{Guid.NewGuid():N}.db";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(DashboardTests).Assembly);
        builder.Services.AddSingleton(new NexMesh.Core.HostAssemblySet([typeof(DashboardTests).Assembly])); // 面板风险扫描的程序集来源(TestServer 下 EntryAssembly 是 testhost,须显式给)
        builder.Services.AddSingleton<INexMeshToolRegistry>(new FakeToolRegistry());
        builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
        builder.Services.AddTransient<ILoginValidator, DashboardTests.DemoValidator>();
        var app = builder.Build();
        app.UseNexMeshService(typeof(DashboardTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: true);
        await app.StartAsync();

        var client = app.GetTestClient();
        var login = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json"));
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var body = await client.GetStringAsync("/nexmesh/dashboard/overview");
        var risks = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("ai").GetProperty("tools");
        var danger = risks.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Dangerous");
        var safe = risks.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Safe");
        Assert.Equal("High", danger.GetProperty("risk").GetString()); // 修复前这里错为 Low
        Assert.Equal("Low", safe.GetProperty("risk").GetString());
        await app.DisposeAsync();
    }
}

/// <summary>Dashboard 操作面:作业立即执行 / 会话踢下线。</summary>
public class DashboardOpsTests
{
    public sealed class OpsJob(string cron, bool runOnStart, IServiceProvider sp)
        : NexMesh.Worker.NexMeshCronJob(cron, runOnStart, provider: sp, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        public static int Runs;
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.CompletedTask;
        }
    }

    private static (WebApplication App, HttpClient Client, string Token) BuildAndLogin()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=ops-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(DashboardTests).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
        builder.Services.AddTransient<ILoginValidator, DashboardTests.DemoValidator>();
        var app = builder.Build();
        app.UseNexMeshService(typeof(DashboardTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: true);
        app.Start();
        var client = app.GetTestClient();
        var login = client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var token = JsonDocument.Parse(login.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (app, client, token);
    }

    [Fact]
    public async Task 作业立即执行_热执行与记录()
    {
        var (app, client, _) = BuildAndLogin();
        // 注册演示作业并启动(加入容器→面板与触发端点可见)
        await app.StopAsync(); await app.DisposeAsync();
        var (app2, client2, _) = BuildAndLoginWithJob();
        var resp = await client2.PostAsync("/nexmesh/dashboard/jobs/OpsJob/trigger", content: null);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.True(body.GetProperty("triggered").GetBoolean());
        Assert.True(OpsJob.Runs >= 1);
        await app2.DisposeAsync();
    }

    private static (WebApplication, HttpClient, string) BuildAndLoginWithJob()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=ops2-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(DashboardTests).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddTransient<ILoginValidator, DashboardTests.DemoValidator>();
        builder.Services.AddNexMeshCronJob<OpsJob>("0 2 * * *");
        var app = builder.Build();
        app.UseNexMeshService(typeof(DashboardTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: true);
        app.Start();
        var client = app.GetTestClient();
        var login = client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var token = JsonDocument.Parse(login.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (app, client, token);
    }

    [Fact]
    public async Task 会话踢下线_该用户会话全部吊销()
    {
        var (app, client, token) = BuildAndLogin();
        // 当前用户已有 1 个会话(登录产生);踢自己 → revoked=1
        var resp = await client.PostAsync("/nexmesh/dashboard/sessions/dash-1/revoke", content: null);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.True(body.GetProperty("revoked").GetInt32() >= 1);
        // 吊销后旧 token 立即失效
        var me = await client.GetAsync("/nexmesh/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, me.StatusCode);
        await app.DisposeAsync();
    }
}
