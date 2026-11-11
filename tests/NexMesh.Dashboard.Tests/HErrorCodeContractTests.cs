namespace NexMesh.Dashboard.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.Auth;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Context;
using NexMesh.Core;
using NexMesh.Host;
using NexMesh.Core.DependencyInjection;
using Xunit;

/// <summary>
/// H 错误码契约夹具:覆盖 401(网关/字面量/坏 Token)、403(网关角色/字面量)、404/405(虚拟网关)、
/// 40001(参数绑定)、500(兜底/Infrastructure 不暴露)、Validation(errors 字典)、429(端点限流信封)。
/// </summary>
public interface IHErrorCodeApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("ping")]
    [EnableRateLimiting("h-code-burst")]
    Task<string> PingAsync();

    [HttpGet("admin-only")]
    [Authorize(Roles = "admin")]
    Task<string> AdminOnlyAsync();

    [HttpGet("arg")]
    Task<long> ArgAsync(long id);

    [HttpGet("boom")]
    Task<string> BoomAsync();

    [HttpGet("infra-boom")]
    Task<string> InfraBoomAsync();

    [HttpGet("validate")]
    Task<string> ValidateAsync();
}

public class HErrorCodeService : IHErrorCodeApi, IScopedService
{
    public Task<string> PingAsync() => Task.FromResult("pong");

    public Task<string> AdminOnlyAsync() => Task.FromResult("admin-ok");

    public Task<long> ArgAsync(long id) => Task.FromResult(id);

    public Task<string> BoomAsync() => throw new InvalidOperationException("数据库连接串 secret-conn-hdetail 不可达");

    public Task<string> InfraBoomAsync() => throw new InfrastructureException("db password=p@ss-hdetail 不可达");

    public Task<string> ValidateAsync() => throw new ValidationException("字段校验失败",
        new Dictionary<string, string[]> { ["name"] = ["不能为空"] });
}

/// <summary>
/// H 第二轮交叉测试:错误码契约矩阵(docs/错误码参考.md 承诺 vs 实测信封)。
/// 漂移备案:MeshException.ErrorCode(40001/40401/40903/50004 等细粒度码)从不进入响应信封——
/// MeshExceptionMiddleware.BuildError 用 ex.StatusCode(HTTP 状态码数值)写 Code,文档表格的五位数码
/// 与信封实测不一致(详见交付报告对照表);本测试固化实测行为,作为契约权威。
/// </summary>
public class HErrorCodeContractTests
{
    private sealed class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password)
            => Task.FromResult<LoginUser?>(username == "demo" && password == "demo123"
                ? new("h-1", "契约测试用户")
                : null);
    }

    private static (WebApplication App, HttpClient Client) BuildGatewayHost(bool withAuth = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "false"; // 网关默认拒绝语义
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true"; // 程序集含 High 风险夹具
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=hcode-gw-{Guid.NewGuid():N}.db";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(HErrorCodeContractTests).Assembly);
        if (withAuth)
        {
            builder.Services.AddNexMeshAuth(builder.Configuration);
            builder.Services.AddTransient<ILoginValidator, DemoValidator>();
        }
        var app = builder.Build();
        // 手工管线:仅虚拟网关模式(不 Map 字面量端点),401/403/404/405 走网关判定
        app.UseNexMeshContext();
        app.UseNexMeshExceptionHandling();
        app.MapNexMeshContractGateway(typeof(HErrorCodeContractTests).Assembly);
        if (withAuth)
        {
            app.MapNexMeshAuth();
        }
        app.Start();
        return (app, app.GetTestClient());
    }

    private static (WebApplication App, HttpClient Client) BuildFullHost(bool withValidator = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
        builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=hcode-full-{Guid.NewGuid():N}.db";
        builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:RateLimit:Policies:h-code-burst:PermitLimit"] = "1",
            ["NexMesh:RateLimit:Policies:h-code-burst:WindowSeconds"] = "60",
        });
        builder.WebHost.UseTestServer();
        builder.AddNexMeshService(typeof(HErrorCodeContractTests).Assembly);
        builder.Services.AddNexMeshRateLimiter(builder.Configuration);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        if (withValidator)
        {
            builder.Services.AddTransient<ILoginValidator, DemoValidator>();
        }
        var app = builder.Build();
        app.UseNexMeshService(typeof(HErrorCodeContractTests).Assembly);
        app.MapNexMeshAuth();
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

    // ── 虚拟网关模式:401 / 403 / 404 / 405 ─────────────────────────────

    [Fact]
    public async Task 网关401_匿名访问受限端点_信封code401与文档一致()
    {
        var (app, client) = BuildGatewayHost();
        var response = await client.GetAsync("/api/herrorcode/admin-only");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Trace-Id"), "文档承诺:响应头含 X-Trace-Id");

        var body = Parse(response.Content);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(401, body.GetProperty("code").GetInt32());
        Assert.Equal("该操作需要登录(网关默认拒绝)", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 网关403_登录但角色不足_信封code403()
    {
        var (app, client) = BuildGatewayHost();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        var response = await client.GetAsync("/api/herrorcode/admin-only");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = Parse(response.Content);
        Assert.Equal(403, body.GetProperty("code").GetInt32());
        Assert.Equal("权限不足(网关角色校验)", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 网关404_未注册逻辑路由_信封code40400_文档对齐()
    {
        var (app, client) = BuildGatewayHost();
        var response = await client.GetAsync("/api/herrorcode/not-registered");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = Parse(response.Content);
        // 【修复反转】网关未注册路由 → 信封 code=40400(文档对齐;HTTP 状态仍 404)
        Assert.Equal(40400, body.GetProperty("code").GetInt32());
        Assert.Contains("未注册", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 网关405_动词不支持_信封code40500_文档对齐()
    {
        var (app, client) = BuildGatewayHost();
        var response = await client.PostAsync("/api/herrorcode/ping", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var body = Parse(response.Content);
        // 【修复反转】动词不支持 → 信封 code=40500(文档对齐;HTTP 状态仍 405)
        Assert.Equal(40500, body.GetProperty("code").GetInt32());
        Assert.Contains("不支持 POST", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    // ── 字面量模式 + 全局异常中间件:40001 / 500 / Validation / 坏 Token / 429 ──

    [Fact]
    public async Task 参数绑定错误_信封code40001_文档对齐()
    {
        var (app, client) = BuildFullHost();
        var response = await client.GetAsync("/api/herrorcode/arg?id=abc");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = Parse(response.Content);
        // 【修复反转】信封 Code=细粒度 ErrorCode(文档对齐;HTTP 状态仍 400)
        Assert.Equal(40001, body.GetProperty("code").GetInt32());
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("""参数 id 格式错误:"abc" 无法转换为 Int64""", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 未知异常兜底_500_不暴露内部细节_TraceId可关联()
    {
        var (app, client) = BuildFullHost();
        var response = await client.GetAsync("/api/herrorcode/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Trace-Id"));

        var body = Parse(response.Content);
        // 【修复反转】未知异常兜底 → 信封 code=50000(文档对齐;HTTP 状态仍 500)
        Assert.Equal(50000, body.GetProperty("code").GetInt32());
        Assert.False(body.GetProperty("success").GetBoolean());
        // SSS+ 安全铁律:内部消息(连接串)绝不回传
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-conn-hdetail", raw);
        Assert.StartsWith("服务器内部错误(TraceId:", body.GetProperty("message").GetString());
        // 文档承诺:message 里的 TraceId 与 X-Trace-Id 头一致,可关联服务端日志
        var headerTrace = response.Headers.GetValues("X-Trace-Id").First();
        Assert.Contains(headerTrace, body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task InfrastructureException_500_消息不暴露()
    {
        var (app, client) = BuildFullHost();
        var response = await client.GetAsync("/api/herrorcode/infra-boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("p@ss-hdetail", raw); // InfrastructureException.ShouldExposeMessage=false
        var body = Parse(response.Content);
        Assert.Equal(500, body.GetProperty("code").GetInt32());
        Assert.StartsWith("服务器内部错误(TraceId:", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task ValidationException_400_errors字段级字典与文档一致()
    {
        var (app, client) = BuildFullHost();
        var response = await client.GetAsync("/api/herrorcode/validate");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = Parse(response.Content);
        Assert.Equal(400, body.GetProperty("code").GetInt32());
        Assert.Equal("字段校验失败", body.GetProperty("message").GetString());
        var errors = body.GetProperty("errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.Equal("不能为空", errors.GetProperty("name")[0].GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 坏Bearer令牌_401_消息与文档一致()
    {
        var (app, client) = BuildFullHost();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "h-invalid-token");
        var response = await client.GetAsync("/api/herrorcode/admin-only");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = Parse(response.Content);
        // 文档 401 行:Token 无效/过期/吊销 → "访问令牌无效、已过期或已吊销" —— 实测一致 ✓
        Assert.Equal(401, body.GetProperty("code").GetInt32());
        Assert.Equal("访问令牌无效、已过期或已吊销", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 端点限流超限_429_信封code429消息可判别()
    {
        var (app, client) = BuildFullHost();
        var first = await client.GetAsync("/api/herrorcode/ping");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var second = await client.GetAsync("/api/herrorcode/ping");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var body = Parse(second.Content);
        // 文档:前端按 code=429 分支 → 实测信封 code=429 ✓(OnRejected 统一信封)
        Assert.Equal(429, body.GetProperty("code").GetInt32());
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("请求过于频繁,请稍后再试", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 未配置ILoginValidator_登录返回50004_文档对齐()
    {
        var (app, client) = BuildFullHost(withValidator: false);
        var response = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "x" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = Parse(response.Content);
        // 【修复反转】信封 Code=50004(文档对齐;HTTP 状态仍 500)
        Assert.Equal(50004, body.GetProperty("code").GetInt32());
        Assert.Equal("未配置 ILoginValidator,业务侧需实现并注册登录验证", body.GetProperty("message").GetString());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task 字面量模式403_角色不足_返回ApiResult信封()
    {
        // 【修复反转】NexMeshTokenAuthHandler 补 HandleForbiddenAsync → 403 信封(此前空 body)
        var (app, client) = BuildFullHost();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client));
        var response = await client.GetAsync("/api/herrorcode/admin-only");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(403, json.GetProperty("code").GetInt32());
        await app.DisposeAsync();
    }
}
