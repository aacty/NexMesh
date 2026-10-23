namespace NexMesh.Auth.Tests;

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;

public class TestUserInfo
{
    public string? Id { get; set; }
    public string? Account { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
}

public class ClaimsMapperTests
{
    [Fact]
    public void 用户模型_映射为声明_Id进NameIdentifier_角色逐条展开()
    {
        var user = new TestUserInfo
        {
            Id = "u-1",
            Name = "张三",
            Email = "zs@test.com",
            Roles = ["Admin", "Doctor"],
            Permissions = ["sys:user:create", "sys:user:remove"],
        };

        var claims = UserClaimsMapper.ToClaims(user);

        Assert.Equal("u-1", claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal("张三", claims.Single(c => c.Type == ClaimTypes.Name).Value);
        Assert.Equal("zs@test.com", claims.Single(c => c.Type == ClaimTypes.Email).Value);
        Assert.Equal(["Admin", "Doctor"], claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));
        Assert.Equal("""["sys:user:create","sys:user:remove"]""", claims.Single(c => c.Type == "permissions").Value);
    }

    [Fact]
    public void 声明_还原为用户模型_角色与集合属性完整()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u-9"),
            new(ClaimTypes.Name, "李四"),
            new(ClaimTypes.Email, "ls@test.com"),
            new("account", "lisi"),
            new(ClaimTypes.Role, "Admin"),
            new(ClaimTypes.Role, "Doctor"),
            new("permissions", """["a","b"]"""),
        };

        var user = UserClaimsMapper.FromClaims<TestUserInfo>(claims);

        Assert.NotNull(user);
        Assert.Equal("u-9", user.Id);
        Assert.Equal("李四", user.Name);
        Assert.Equal("lisi", user.Account);
        Assert.Equal(["Admin", "Doctor"], user.Roles);
        Assert.Equal(["a", "b"], user.Permissions);
    }
}

public class JwtTokenServiceTests
{
    private static (IJwtTokenService Service, NexMeshTokenService Tokens) Create()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Authentication:Jwt:AccessTokenExpirationMinutes"] = "60",
            ["NexMesh:Authentication:Jwt:RefreshTokenExpirationDays"] = "7",
        }).Build();
        var services = new ServiceCollection();
        services.AddNexMeshAuth(config);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IJwtTokenService>(),
            provider.GetRequiredService<NexMeshTokenService>());
    }

    [Fact]
    public async Task 签发访问令牌_会话带多值声明_可校验()
    {
        var (service, tokens) = Create();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u-1"),
            new(ClaimTypes.Name, "张三"),
            new(ClaimTypes.Role, "Admin"),
            new(ClaimTypes.Role, "Doctor"),
        };

        var token = service.GenerateAccessToken(claims);

        Assert.True(service.ValidateToken(token));
        var session = await tokens.GetAsync(token);
        Assert.NotNull(session);
        Assert.Equal(2, session!.ClaimList!.Count(c => c.Type == ClaimTypes.Role));
    }

    [Fact]
    public void 过期令牌_仍可重建ClaimsPrincipal_刷新流程可用()
    {
        var (service, _) = Create();
        var token = service.GenerateAccessToken(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u-1"),
            new(ClaimTypes.Role, "Doctor"),
        });

        var principal = service.GetPrincipalFromExpiredToken(token);

        Assert.NotNull(principal);
        Assert.Equal("u-1", principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(principal.IsInRole("Doctor"));
    }

    [Fact]
    public void 刷新令牌_信息完整_按userId校验()
    {
        var (service, _) = Create();
        var refresh = service.GenerateRefreshToken("u-1");

        Assert.Equal("u-1", refresh.UserId);
        Assert.False(string.IsNullOrWhiteSpace(refresh.Token));
        Assert.False(refresh.IsExpired);
    }

    [Fact]
    public async Task 泛型扩展_用户模型直接签发()
    {
        var (service, _) = Create();
        var user = new TestUserInfo { Id = "u-3", Name = "王五", Roles = ["Admin"] };

        var token = service.GenerateAccessToken(user);
        var principal = service.GetPrincipalFromExpiredToken(token);

        Assert.NotNull(principal);
        Assert.Equal("u-3", principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(principal.IsInRole("Admin"));
    }
}

public class MemoryTokenStoreTests
{
    [Fact]
    public async Task 白名单_存取校验移除_全链路()
    {
        var services = new ServiceCollection();
        services.AddNexMeshAuth(new ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ITokenStore>();
        var sessions = provider.GetRequiredService<ISessionStore>();
        var tokens = provider.GetRequiredService<NexMeshTokenService>();

        // 签发真实会话(模拟登录)
        var session = await tokens.IssueAsync(new LoginUser("u-1", "张三", null, new Dictionary<string, string>()));
        await store.StoreAccessTokenAsync("u-1", session.Token, TimeSpan.FromMinutes(60));
        await store.StoreRefreshTokenAsync("u-1", "rt-1", TimeSpan.FromDays(7));

        Assert.True(await store.ValidateAccessTokenAsync("u-1", session.Token));
        Assert.True(await store.ValidateRefreshTokenAsync("u-1", "rt-1"));
        Assert.False(await store.ValidateRefreshTokenAsync("u-1", "rt-forged"));

        // 移除访问令牌 → 会话真实吊销
        await store.RemoveAccessTokenAsync("u-1", session.Token);
        Assert.False(await store.ValidateAccessTokenAsync("u-1", session.Token));
        Assert.Null(await sessions.GetAsync(session.Token));

        // 全端登出 → 刷新令牌与索引清空
        await store.RemoveAllTokensAsync("u-1");
        Assert.False(await store.ValidateRefreshTokenAsync("u-1", "rt-1"));
    }
}

/// <summary>端到端:登录签发 → Bearer 认证 → ICurrentUser&lt;TUser&gt; 还原业务用户模型(含角色/集合属性)。</summary>
public class CurrentUserGenericTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public class DemoValidator : ILoginValidator
    {
        public Task<LoginUser?> ValidateAsync(string username, string password) =>
            Task.FromResult<LoginUser?>(username == "doctor" && password == "123456"
                ? new LoginUser("doc-1", "张三", "hosp-A")
                : null);
    }

    public interface IWhoApi : IServiceApi
    {
        Task<string> WhoAsync();
    }

    public class WhoService(ICurrentUser<TestUserInfo> user) : IWhoApi
    {
        public Task<string> WhoAsync() => Task.FromResult(
            $"{user.User?.Id}|{user.User?.Name}|{string.Join(",", user.Roles)}|{user.IsAuthenticated}");
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshContracts(typeof(WhoService).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration);
        builder.Services.AddSingleton<IJwtTokenService>((sp) =>
            new NexMeshJwtTokenService(
                sp.GetRequiredService<NexMeshTokenService>(),
                sp.GetRequiredService<IClaimsJournal>(),
                Microsoft.Extensions.Options.Options.Create(new AuthenticationOptions())));
        builder.Services.AddSingleton<ILoginValidator, DemoValidator>();

        _app = builder.Build();
        _app.UseNexMeshAuthentication();
        _app.UseAuthorization();
        _app.MapNexMeshContracts(typeof(WhoService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task 强类型当前用户_从令牌还原业务模型()
    {
        // 用 IJwtTokenService 直接签发(等价于业务 AuthService 登录后签发的令牌形态)
        var jwt = _app.Services.GetRequiredService<IJwtTokenService>();
        var token = jwt.GenerateAccessToken(new TestUserInfo
        {
            Id = "doc-1",
            Name = "张三",
            Roles = ["Doctor"],
            Permissions = ["patient:read"],
        });

        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/api/Who/Who");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("doc-1|张三|Doctor|True", body);
    }
}
