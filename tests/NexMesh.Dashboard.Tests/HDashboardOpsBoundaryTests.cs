namespace NexMesh.Dashboard.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using FreeSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.AI.Approval;
using NexMesh.Auth;
using NexMesh.Data.Migrations;
using NexMesh.Host;
using Xunit;

/// <summary>H 迁移边界夹具:一个空操作迁移(用于"无待应用"场景)。</summary>
public sealed class HBoundaryMigration : NexMeshMigration
{
    public override long Version => 20991001001;
    public override string Name => "H边界演示迁移";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// H 第二轮交叉测试:Dashboard 操作端点边界(不存在/歧义/空集场景)。
/// 全部端点应返回结构化业务码信封(非 500 兜底)。
/// 发现的缺陷详见各测试注释(P1:缓存源名不存在 → 500)。
/// </summary>
public class HDashboardOpsBoundaryTests
{
    private sealed class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password)
            => Task.FromResult<LoginUser?>(username == "demo" && password == "demo123"
                ? new("h-ops-1", "边界测试用户")
                : null);
    }

    private static (WebApplication App, HttpClient Client) Build(bool withMigrations = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true"; // 程序集含 High 风险夹具
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=hops-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration["NexMesh:Cache:Mode"] = "Memory";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(HDashboardOpsBoundaryTests).Assembly);
        if (withMigrations)
        {
            builder.Services.AddNexMeshMigrations(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                typeof(HDashboardOpsBoundaryTests).Assembly);
        }
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
        builder.Services.AddTransient<ILoginValidator, DemoValidator>();
        var app = builder.Build();
        app.UseNexMeshService(typeof(HDashboardOpsBoundaryTests).Assembly);
        app.MapNexMeshAuth();
        app.MapNexMeshDashboard(force: true);
        app.Start();
        return (app, app.GetTestClient());
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static JsonElement Parse(HttpContent content)
        => JsonDocument.Parse(content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;

    // ── 作业触发 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 作业触发_不存在作业名_结构化404业务码非500()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.PostAsync("/nexmesh/dashboard/jobs/no-such-job/trigger", content: null);
        // 【修复反转】HTTP 状态与信封 code 对齐(404/404)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = Parse(response.Content);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(404, body.GetProperty("code").GetInt32());
        Assert.Contains("未找到作业 no-such-job", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    // ── 会话踢下线 ───────────────────────────────────────────────────

    [Fact]
    public async Task 会话踢下线_不存在用户_revoked0结构化返回()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.PostAsync("/nexmesh/dashboard/sessions/no-such-user/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Parse(response.Content);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(0, body.GetProperty("data").GetProperty("revoked").GetInt32());
        // 备案(语义观察,非缺陷):success=true + revoked=0——"用户不存在"与"用户已无活跃会话"
        // 在信封上不可区分;运维面板需自行解读 revoked 计数
        await app.DisposeAsync();
    }

    // ── 缓存键巡视 ───────────────────────────────────────────────────

    [Fact]
    public async Task 缓存键列表_内存模式诚实空集()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.GetAsync("/nexmesh/dashboard/cache/main/keys");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Parse(response.Content);
        Assert.Equal(0, body.GetProperty("data").GetArrayLength()); // 内存模式诚实降级空集
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 缓存键删除_内存模式_诚实返回_不存在键为false()
    {
        // 【修复反转】内存分支先查存在再删:不存在键 removed=false,与 Redis KeyDeleteAsync 语义一致
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.DeleteAsync("/nexmesh/dashboard/cache/main/keys/no-such-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Parse(response.Content);
        Assert.False(body.GetProperty("data").GetProperty("removed").GetBoolean());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 缓存键列表_源名不存在_结构化400_修复反转()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.GetAsync("/nexmesh/dashboard/cache/no-such-source/keys");
        // 【修复反转】未知源名 → 结构化 400,消息含源名(运维可定位;此前 500 通用文案吞掉)
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("no-such-source", raw);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 缓存键删除_源名不存在_结构化400_修复反转()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.DeleteAsync("/nexmesh/dashboard/cache/no-such-source/keys/whatever");
        // 【修复反转】未知源名 → 结构化 400,消息含源名
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("no-such-source", raw);
        await app.DisposeAsync();
    }

    // ── 迁移在线执行 ─────────────────────────────────────────────────

    [Fact]
    public async Task 迁移在线执行_无待应用_applied0结构化返回()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        await app.Services.GetRequiredService<NexMesh.Data.Migrations.IMigrationRunner>().RunPendingAsync(); // 先应用完

        var response = await client.PostAsync("/nexmesh/dashboard/migrations/run", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Parse(response.Content);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(0, body.GetProperty("data").GetProperty("applied").GetInt32());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 迁移在线执行_迁移未启用_结构化400业务码非500()
    {
        var (app, client) = Build(withMigrations: false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.PostAsync("/nexmesh/dashboard/migrations/run", content: null);
        // 备案:HTTP 200 + body code=400(同作业触发端点,HTTP 与 code 不对齐的信封内结构化)
        var body = Parse(response.Content);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(400, body.GetProperty("code").GetInt32());
        Assert.Contains("迁移未启用", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    // ── 审批操作 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 审批决策_不存在审批单_HTTP404_code40401_文档对齐()
    {
        var (app, client) = Build();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));

        var response = await client.PostAsync("/nexmesh/dashboard/approvals/h-nope/approve", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = Parse(response.Content);
        // ApprovalDecisionService 抛 MeshException(…, 40401, 404)
        // 【修复反转】信封 Code=40401(文档对齐;HTTP 状态仍 404)
        Assert.Equal(40401, body.GetProperty("code").GetInt32());
        Assert.Equal("审批单 h-nope 不存在", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }
}
