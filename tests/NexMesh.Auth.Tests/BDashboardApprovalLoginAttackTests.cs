namespace NexMesh.Auth.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI.Approval;
using NexMesh.AI; // AddNexMeshAi
using NexMesh.Core;
using NexMesh;
using NexMesh.Core.DependencyInjection;
using NexMesh.Host; // MapNexMeshDashboard
using Xunit;

/// <summary>
/// 攻击面 3(框架架构师 B 对抗测试):Dashboard 管理台与审批端点的匿名攻击面。
/// 锁定语义:未带 token 一律 401 业务信封(含 approve/reject 高危操作);静态页匿名可载属文档声明行为。
/// 附:合法 token 下 approve 走通(证明 401 是授权门而非路由缺失)。
/// </summary>
public class BDashboardApprovalAuthzTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password) =>
            Task.FromResult<LoginUser?>(username == "doctor" && password == "123456"
                ? new LoginUser("doc-1", "医生", null)
                : null);
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub",
            ["NexMesh:AI:Approval:Enabled"] = "true",
        });
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshConventionServices(typeof(BDashboardApprovalAuthzTests).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddSingleton<ILoginValidator, DemoValidator>();
        builder.Services.AddAuthorization();
        builder.Services.AddNexMeshAi(builder.Configuration, typeof(BDashboardApprovalAuthzTests).Assembly);
        builder.Services.AddNexMeshApproval(builder.Configuration);
        builder.Services.AddNexMeshDashboard();

        _app = builder.Build();
        _app.UseNexMeshAuthentication();
        _app.UseAuthorization();
        _app.MapNexMeshAuth();
        _app.MapNexMeshApprovals(requireAuthorization: true);
        _app.MapNexMeshDashboard(force: true);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<string> LoginAsync()
    {
        var response = await _client.PostAsync("/nexmesh/auth/login",
            new StringContent("""{"username":"doctor","password":"123456"}""", Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private async Task<ApprovalRequest> CreatePendingAsync()
    {
        var store = _app.Services.GetRequiredService<IApprovalStore>();
        return await store.AddAsync(new ApprovalRequest
        {
            ToolName = "file.delete",
            ArgumentsJson = """{"path":"/tmp/x"}""",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
        });
    }

    [Fact]
    public async Task dashboard_overview_匿名401()
    {
        var response = await _client.GetAsync("/nexmesh/dashboard/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":401", (await response.Content.ReadAsStringAsync()).Replace(" ", ""));
    }

    [Fact]
    public async Task dashboard_审批数据与操作_匿名一律401()
    {
        var pending = await CreatePendingAsync();
        var id = pending.Id;

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/nexmesh/dashboard/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsync($"/nexmesh/dashboard/approvals/{id}/approve", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsync($"/nexmesh/dashboard/approvals/{id}/reject", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task dashboard_高危操作_匿名401_作业触发与会话吊销与缓存删除与迁移()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsync("/nexmesh/dashboard/jobs/any-job/trigger", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsync("/nexmesh/dashboard/sessions/alice/revoke", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.DeleteAsync("/nexmesh/dashboard/cache/main/keys/some-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.PostAsync("/nexmesh/dashboard/migrations/run", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task 审批中心api_approve_匿名401_带token放行走通()
    {
        var pending = await CreatePendingAsync();

        var anonymous = await _client.PostAsync($"/api/approvals/{pending.Id}/approve", new StringContent(""));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/approvals/pending")).StatusCode);

        // 带 token:401 门打开(决策走原子认领;工具未注册 → 业务失败信封,但 HTTP 200=已过授权门)
        var token = await LoginAsync();
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post,
            $"/api/approvals/{pending.Id}/approve");
        request.Headers.Authorization = new("Bearer", token);
        var ok = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = (await ok.Content.ReadAsStringAsync()).Replace(" ", "");
        Assert.Contains("\"success\":true", body);
    }

    [Fact]
    public async Task dashboard_静态页_匿名可载_文档声明行为()
    {
        var response = await _client.GetAsync("/nexmesh/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// 攻击面 4(框架架构师 B 对抗测试):登录端点滥用。
/// 锁定语义:①错误密码与不存在用户返回逐字节相同的 401 信封(用户存在性不泄漏);
/// ②内存限流 per-IP 固定窗口:连发超限 429,且超限后正确密码同样被拒(暴力破解无侧信道)。
/// </summary>
public class BLoginAbuseTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public class EnumeratingValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password) =>
            Task.FromResult<LoginUser?>(username == "alice" && password == "right-pass"
                ? new LoginUser("alice-1", "爱丽丝", null)
                : null); // 真实语义:未知用户与错密码都返回 null
    }

    private async Task InitializeCoreAsync(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddSingleton<ILoginValidator, EnumeratingValidator>();
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseNexMeshAuthentication();
        _app.UseAuthorization();
        _app.MapNexMeshAuth();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    private async Task<(HttpStatusCode Status, string Body)> LoginAsync(string user, string pass)
    {
        var response = await _client.PostAsync("/nexmesh/auth/login",
            new StringContent($$"""{"username":"{{user}}","password":"{{pass}}"}""", Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ParseField(string json, string field)
        => JsonSerializer.Deserialize<JsonElement>(json).GetProperty(field).GetRawText();

    [Fact]
    public async Task 错误密码与不存在用户_401信封逐字节相同_用户存在性不泄漏()
    {
        await InitializeCoreAsync(new Dictionary<string, string?>
        {
            ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "100", // 本测试只看 401 语义,先解除限流干扰
        });
        try
        {
            var wrongPassword = await LoginAsync("alice", "wrong-pass");
            var unknownUser = await LoginAsync("ghost-user", "whatever");

            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
            Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.Status);
            // 信封含逐请求 Timestamp(无攻击价值)——攻击者可对比的是语义字段:比较 message/code 逐字节一致
            Assert.Equal(ParseField(wrongPassword.Body, "message"), ParseField(unknownUser.Body, "message"));
            Assert.Equal(ParseField(wrongPassword.Body, "code"), ParseField(unknownUser.Body, "code"));
            Assert.Contains("用户名或密码错误", ParseField(wrongPassword.Body, "message"));
            Assert.DoesNotContain("alice", unknownUser.Body);
            Assert.DoesNotContain("ghost", unknownUser.Body);
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 连发错误密码_内存限流超限429_且正确密码在超限后同样被拒()
    {
        await InitializeCoreAsync(new Dictionary<string, string?>
        {
            ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "2",
            ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "60",
        });
        try
        {
            var first = await LoginAsync("alice", "bad-1");
            var second = await LoginAsync("alice", "bad-2");
            Assert.Equal(HttpStatusCode.Unauthorized, first.Status);
            Assert.Equal(HttpStatusCode.Unauthorized, second.Status);

            var third = await LoginAsync("alice", "bad-3");
            Assert.Equal(HttpStatusCode.TooManyRequests, third.Status);
            Assert.Contains("过于频繁", third.Body);

            // 超限后即使密码正确也 429:限流先于凭据校验(暴力破解无法借正确凭据穿透)
            var blocked = await LoginAsync("alice", "right-pass");
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.Status);
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }
}
