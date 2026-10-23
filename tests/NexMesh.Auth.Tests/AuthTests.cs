namespace NexMesh.Auth.Tests;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Approval;
using NexMesh.AI; // AddNexMeshAi
using NexMesh.Core;
using NexMesh.Core.AutoApi;
using NexMesh.Core.DependencyInjection;
using NexMesh.Host; // UseNexMeshService

public class AuthTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public static int SlidingTouches;

    /// <summary>受保护业务接口:实现类标注 [Authorize](C# 限制:该特性不能标接口)。</summary>
    public interface ISecretBiz : IAutoApi
    {
        Task<string> WhoAsync();
    }

    public interface IProtectedBiz : IAutoApi
    {
        Task<string> ProtectedAsync();
    }

    [ServiceRoute("api/secret")]
    public class SecretService(ICurrentUser user) : ISecretBiz, IScopedService
    {
        public Task<string> WhoAsync() =>
            Task.FromResult($"{user.UserId}|{user.UserName}|{user.TenantId}|auth={user.IsAuthenticated}");
    }

    [Microsoft.AspNetCore.Authorization.Authorize]
    [ServiceRoute("api/protected")]
    public class ProtectedService : IProtectedBiz, IScopedService
    {
        public Task<string> ProtectedAsync() => Task.FromResult("protected-ok");
    }

    public class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password) =>
            Task.FromResult<LoginUser?>(
                username == "doctor" && password == "123456"
                    ? new LoginUser("doc-001", "张三医生", "hosp-A",
                        new Dictionary<string, string> { ["role"] = "doctor" })
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
            ["NexMesh:Auth:AccessTokenExpireMinutes"] = "720",
        });
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshConventionServices(typeof(SecretService).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddSingleton<ILoginValidator, DemoValidator>();
        builder.Services.AddAuthorization();
        builder.Services.AddNexMeshAi(builder.Configuration, typeof(SecretService).Assembly);
        builder.Services.AddNexMeshApproval(builder.Configuration);

        _app = builder.Build();
        _app.UseNexMeshAuthentication(); // 认证在上下文快照之前:Token 声明进入快照/ICurrentUser
        _app.UseAuthorization();         // 显式钉住顺序:授权必须在认证之后(隐式插入位置不可依赖)
        _app.UseNexMeshService(typeof(SecretService).Assembly);
        _app.MapNexMeshAuth();
        _app.MapNexMeshApprovals(requireAuthorization: true);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<string?> LoginAsync(string user = "doctor", string pass = "123456")
    {
        var response = await _client.PostAsync("/api/auth/login",
            new StringContent($"{{\"username\":\"{user}\",\"password\":\"{pass}\"}}", Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("data").GetProperty("accessToken").GetString();
    }

    [Fact]
    public async Task 诊断_签发后容器内可查会话()
    {
        var token = await LoginAsync();
        Assert.NotNull(token);
        var session = await _app.Services.GetRequiredService<NexMeshTokenService>().GetAsync(token!);
        Assert.NotNull(session); // 存储层 OK → 失败在管线;存储层失败 → 签发/解析问题
    }

    [Fact]
    public async Task 登录成功_返回令牌_身份贯通到ICurrentUser()
    {
        var token = await LoginAsync();
        Assert.NotNull(token);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secret/whoasync");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("doc-001", body);
        Assert.Contains("张三医生", body);
        Assert.Contains("hosp-A", body); // 租户贯通:Token → 中间件 → 快照 → ICurrentUser
    }

    [Fact]
    public async Task 密码错误_401业务码()
    {
        var response = await _client.PostAsync("/api/auth/login",
            new StringContent("""{"username":"doctor","password":"wrong"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("40102", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 受保护AutoApi_无Token拒绝_有效Token放行()
    {
        // 接口标注 [Authorize] → 端点受保护
        var anonymous = await _client.PostAsync("/api/protected/protectedasync", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/protected/protectedasync");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        Assert.Contains("protected-ok", body);
    }

    [Fact]
    public async Task 无效与伪造Token_统一401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secret/whoasync");
        request.Headers.Authorization = new("Bearer", "forged-token-value");
        var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("40101", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 登出吊销_令牌立即失效()
    {
        var token = await LoginAsync();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Authorization = new("Bearer", token);
        Assert.True((await _client.SendAsync(logout)).IsSuccessStatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secret/whoasync");
        request.Headers.Authorization = new("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task 审批中心授权收紧_匿名401_带Token通过()
    {
        var anonymous = await _client.GetAsync("/api/approvals/pending");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/approvals/pending");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("\"data\":[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task me端点_返回当前身份()
    {
        var token = await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        Assert.Contains("doc-001", body);
        Assert.Contains("hosp-A", body);
    }
}

/// <summary>SSS+第二轮 B 组:登录限流(B5)。</summary>
public class LoginRateLimitTests
{
    [Fact]
    public async Task 连续错误登录_超限返回429()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "2",
        }).Build();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshAuth(config);
        builder.Services.AddSingleton<ILoginValidator, AuthTests.DemoValidator>();
        var app = builder.Build();
        app.UseNexMeshAuthentication();
        app.UseAuthorization();
        app.MapNexMeshAuth();
        await app.StartAsync();
        var client = app.GetTestClient();

        var statuses = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsync("/api/auth/login",
                new StringContent("""{"username":"x","password":"y"}""", System.Text.Encoding.UTF8, "application/json"));
            statuses.Add((int)response.StatusCode);
        }

        Assert.Contains(429, statuses); // 超限触发 429(42901)
        await app.DisposeAsync();
    }
}
