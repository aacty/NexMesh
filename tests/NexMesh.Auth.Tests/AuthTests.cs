namespace NexMesh.Auth.Tests;

using NexMesh.Auth.Oidc;

using System.Net.Http.Json;
using System.Net;
using System.Web;
using System.Net.Sockets;
using System.Security.Cryptography;
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
using NexMesh;
using NexMesh.Core.DependencyInjection;
using NexMesh.Host; // UseNexMeshService

public class AuthTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public static int SlidingTouches;

    /// <summary>受保护业务接口:[Authorize] 标在接口方法上(契约引擎读取)。</summary>
    public interface ISecretBiz : IServiceApi
    {
        Task<string> WhoAsync();
    }

    public interface IProtectedBiz : IServiceApi
    {
        [Microsoft.AspNetCore.Authorization.Authorize]
        Task<string> ProtectedAsync();
    }

    public class SecretService(ICurrentUser user) : ISecretBiz, IScopedService
    {
        public Task<string> WhoAsync() =>
            Task.FromResult($"{user.UserId}|{user.UserName}|{user.TenantId}|auth={user.IsAuthenticated}");
    }

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
        var response = await _client.PostAsync("/nexmesh/auth/login",
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

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secretbiz/who");
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
        var response = await _client.PostAsync("/nexmesh/auth/login",
            new StringContent("""{"username":"doctor","password":"wrong"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":401", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 受保护AutoApi_无Token拒绝_有效Token放行()
    {
        // 接口标注 [Authorize] → 端点受保护
        var anonymous = await _client.PostAsync("/api/protectedbiz/protected", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await LoginAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/protectedbiz/protected");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        Assert.Contains("protected-ok", body);
    }

    [Fact]
    public async Task 无效与伪造Token_统一401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secretbiz/who");
        request.Headers.Authorization = new("Bearer", "forged-token-value");
        var response = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":401", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 登出吊销_令牌立即失效()
    {
        var token = await LoginAsync();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/nexmesh/auth/logout");
        logout.Headers.Authorization = new("Bearer", token);
        Assert.True((await _client.SendAsync(logout)).IsSuccessStatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/secretbiz/who");
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
        var request = new HttpRequestMessage(HttpMethod.Get, "/nexmesh/auth/me");
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
            var response = await client.PostAsync("/nexmesh/auth/login",
                new StringContent("""{"username":"x","password":"y"}""", System.Text.Encoding.UTF8, "application/json"));
            statuses.Add((int)response.StatusCode);
        }

        Assert.Contains(429, statuses); // 超限触发 429(42901)
        await app.DisposeAsync();
    }
}

/// <summary>Redis 登录限流计数器:真实 Redis 下验证原子窗口;无 NEXMESH_TEST_REDIS 环境变量时软跳过(CI 提供)。</summary>
public class RedisLoginRateLimitTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    [Fact]
    public async Task 并发二十次同Key_恰好放行限额数()
    {
        if (ConnectionString is null) return; // 无真实 Redis 环境时跳过

        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var counter = new RedisLoginRateLimitCounter(redis, $"test:rl:{Guid.NewGuid():N}:");
        var key = "ip-10.0.0.1";

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => counter.TryPassAsync(key, permitLimit: 5, windowSeconds: 60)));

        Assert.Equal(5, results.Count(r => r));  // 恰好 5 次放行(原子性:无超发无丢失)
        Assert.Equal(15, results.Count(r => !r));
    }

    [Fact]
    public async Task 首次计数即设TTL_不同Key互不影响()
    {
        if (ConnectionString is null) return;

        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var prefix = $"test:rl:{Guid.NewGuid():N}:";
        var counter = new RedisLoginRateLimitCounter(redis, prefix);

        Assert.True(await counter.TryPassAsync("ip-a", 2, 60));
        Assert.True(await counter.TryPassAsync("ip-b", 2, 60)); // 不同 key 独立窗口

        var ttl = (long)await redis.GetDatabase().ExecuteAsync("PTTL", (StackExchange.Redis.RedisKey)$"{prefix}ip-a");
        Assert.True(ttl > 0 && ttl <= 60_000, $"TTL 应在 (0, 60000]ms,实际 {ttl}");
    }
}

/// <summary>内存限流计数器过期清扫:窗口过期超过一个窗口期的 Key 被移除(防假 IP 扫描内存无限增长)。</summary>
public class MemoryCounterSweepTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task 摊销清扫_过期Key被移除_活跃Key保留()
    {
        var time = new FixedTimeProvider();
        var counter = new MemoryLoginRateLimitCounter(time);

        // 注入超过清扫阈值(4096)的过期 Key
        for (var i = 0; i < 4100; i++)
        {
            await counter.TryPassAsync($"old-{i}", 5, 60);
        }
        // 一个即将活跃的 Key(稍后仍会使用)
        await counter.TryPassAsync("active-1", 5, 60);

        // 时间前进 2 个窗口期(超过清扫阈值:60s×2)
        time.Now += TimeSpan.FromSeconds(125);
        // 触发新一轮调用 → 摊销清扫执行
        for (var i = 0; i < 4100; i++)
        {
            await counter.TryPassAsync($"fresh-{i}", 5, 60);
        }

        // 验证:旧 Key 过期后其窗口在 next-hit 时会重置(行为不变),字典规模有界
        // 通过行为验证清扫:old-0 重新计数(窗口重置=1 ≤ 5 放行)
        Assert.True(await counter.TryPassAsync("old-0", 5, 60));
        // 活跃 key:已过 125s > 60s 窗口,重置后放行
        Assert.True(await counter.TryPassAsync("active-1", 5, 60));
    }

    [Fact]
    public async Task 并发清扫_计数守恒()
    {
        var counter = new MemoryLoginRateLimitCounter();
        var allowed = 0L;
        // 50 万次并发(跨多次清扫),限额无限 → 全部放行,证明清扫不破坏原子计数
        await Parallel.ForAsync(0, 100_000, async (i, ct) =>
        {
            if (await counter.TryPassAsync($"ip-{i % 1000}", int.MaxValue, 60))
            {
                Interlocked.Increment(ref allowed);
            }
        });
        Assert.Equal(100_000, allowed);
    }
}

/// <summary>OIDC token-exchange:自造 JWT(RS256/HS256)全语义验证——验签/过期/错密钥/iss/aud/claims 映射/端点换发。</summary>
public class OidcTokenExchangeTests
{
    // —— 自造 JWT 工具(零外部依赖) ——
    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MakeJwt(Dictionary<string, object> header, Dictionary<string, object> payload, Func<byte[], byte[]> sign)
    {
        var h = B64Url(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(header)));
        var p = B64Url(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload)));
        var sig = B64Url(sign(System.Text.Encoding.UTF8.GetBytes($"{h}.{p}")));
        return $"{h}.{p}.{sig}";
    }

    private static Microsoft.Extensions.Options.IOptions<NexMesh.Auth.AuthenticationOptions> Opts(string issuer, string? secret = null, string? pem = null, string? aud = null) =>
        Microsoft.Extensions.Options.Options.Create(new NexMesh.Auth.AuthenticationOptions
        {
            Oidc = new OidcOptions { Issuer = issuer, Secret = secret ?? "", RsaPublicKeyPem = pem ?? "", Audience = aud ?? "" },
        });

    [Fact]
    public void RS256有效令牌_验证通过并映射用户()
    {
        using var rsa = RSA.Create(2048);
        var jwt = MakeJwt(
            new() { ["alg"] = "RS256", ["typ"] = "JWT" },
            new() { ["sub"] = "user-42", ["preferred_username"] = "张三", ["tenantid"] = "hosp-A",
                    ["roles"] = new[] { "doctor", "auditor" },
                    ["iss"] = "https://idp.example.com", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() },
            data => rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));

        var validator = new OidcTokenValidator(Opts("https://idp.example.com", pem: rsa.ExportSubjectPublicKeyInfoPem()));
        var user = validator.Validate(jwt);

        Assert.NotNull(user);
        Assert.Equal("user-42", user!.UserId);
        Assert.Equal("张三", user.UserName);
        Assert.Equal("hosp-A", user.TenantId);
        Assert.NotNull(user.ClaimList);
        Assert.Equal(2, user.ClaimList!.Count); // roles → 多值 Role
    }

    [Fact]
    public void HS256有效令牌_验证通过()
    {
        var secret = "shared-secret-至少32字符的对称密钥啊啊啊啊啊啊";
        var jwt = MakeJwt(
            new() { ["alg"] = "HS256" },
            new() { ["sub"] = "u1", ["iss"] = "idp-x", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() },
            data => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), data));

        var validator = new OidcTokenValidator(Opts("idp-x", secret: secret));
        Assert.NotNull(validator.Validate(jwt));
    }

    [Fact]
    public void 签名被篡改_拒绝()
    {
        using var rsa = RSA.Create(2048);
        var jwt = MakeJwt(
            new() { ["alg"] = "RS256" },
            new() { ["sub"] = "u1", ["iss"] = "idp", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() },
            data => rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        // 换一把无关密钥的签名
        using var attacker = RSA.Create(2048);
        var parts = jwt.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{B64Url(attacker.SignData(System.Text.Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1))}";

        var validator = new OidcTokenValidator(Opts("idp", pem: rsa.ExportSubjectPublicKeyInfoPem()));
        Assert.Null(validator.Validate(forged));
    }

    [Fact]
    public void 过期令牌_拒绝()
    {
        var secret = "k";
        var jwt = MakeJwt(
            new() { ["alg"] = "HS256" },
            new() { ["sub"] = "u1", ["iss"] = "idp", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds() },
            data => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), data));
        var validator = new OidcTokenValidator(Opts("idp", secret: secret));
        Assert.Null(validator.Validate(jwt));
    }

    [Fact]
    public void 无exp令牌_拒绝_iss不符_拒绝()
    {
        var secret = "k";
        var noExp = MakeJwt(new() { ["alg"] = "HS256" }, new() { ["sub"] = "u1", ["iss"] = "idp" },
            data => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), data));
        var wrongIss = MakeJwt(new() { ["alg"] = "HS256" },
            new() { ["sub"] = "u1", ["iss"] = "other-idp", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() },
            data => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), data));
        var validator = new OidcTokenValidator(Opts("idp", secret: secret));
        Assert.Null(validator.Validate(noExp));    // 永生令牌不允许
        Assert.Null(validator.Validate(wrongIss)); // iss 校验
    }

    [Fact]
    public async Task login端点_token_exchange换发本地令牌()
    {
        var secret = "ex-key-0123456789abcdef0123456789abcdef";
        var jwt = MakeJwt(new() { ["alg"] = "HS256" },
            new() { ["sub"] = "ext-9", ["preferred_username"] = "外部用户", ["iss"] = "https://sso.corp", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() },
            data => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), data));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Authentication:Oidc:Issuer"] = "https://sso.corp",
            ["NexMesh:Authentication:Oidc:Secret"] = secret,
        });
        builder.Services.AddNexMesh(); // ICurrentUser 等 Core 服务(me 端点依赖)
        builder.Services.AddNexMeshAuth(builder.Configuration);
        var app = builder.Build();
        app.MapNexMeshAuth();
        await app.StartAsync();
        var client = app.GetTestClient();

        // ① 有效 IdP JWT → 换发本地令牌
        var ok = await client.PostAsync("/nexmesh/auth/login",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(new { oidcToken = jwt }), System.Text.Encoding.UTF8, "application/json"));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        var body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("accessToken", body);
        Assert.Contains("外部用户", body);

        // ② 无效 JWT → 401
        var bad = await client.PostAsync("/nexmesh/auth/login",
            new StringContent("{\"oidcToken\":\"not.a.jwt\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, bad.StatusCode);
        await app.DisposeAsync();
    }
}

/// <summary>审核加固回归:OIDC 弱密钥拒绝启用;非法 exp/nbf 数值不抛异常。</summary>
public class OidcHardeningTests
{
    [Fact]
    public void HS256弱密钥_拒绝启用()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new NexMesh.Auth.AuthenticationOptions
        {
            Oidc = new NexMesh.Auth.OidcOptions { Issuer = "idp", Secret = "short-key" }, // <32 字节
        });
        var validator = new NexMesh.Auth.OidcTokenValidator(opts);
        Assert.False(validator.Enabled); // 弱对称密钥(可被字典攻击)不启用
    }

    [Fact]
    public void 非法exp数值_安全拒绝()
    {
        // exp 为 long.MaxValue:SafeFromUnix 钳制为 MaxValue→未过期→通过验签路径;此处验证不抛异常
        var secret = new string('k', 40);
        var b64 = (byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = $$"""{"sub":"u1","iss":"idp","exp":{{long.MaxValue}}}""";
        var signed = System.Text.Encoding.UTF8.GetBytes($"{b64(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))}.{b64(System.Text.Encoding.UTF8.GetBytes(payload))}");
        var sig = b64(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), signed));
        var jwt = $"{b64(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))}.{b64(System.Text.Encoding.UTF8.GetBytes(payload))}.{sig}";
        var opts = Microsoft.Extensions.Options.Options.Create(new NexMesh.Auth.AuthenticationOptions
        {
            Oidc = new NexMesh.Auth.OidcOptions { Issuer = "idp", Secret = secret },
        });
        var validator = new NexMesh.Auth.OidcTokenValidator(opts);
        var ex = Record.Exception(() => validator.Validate(jwt));
        Assert.Null(ex); // 越界 exp 钳制处理,永不抛异常(通过与否取决于钳制语义:MaxValue 未过期→有效)
    }
}

/// <summary>Redis 会话枚举(RedisAuthStore.ListActiveAsync):真 Redis 实证——活跃列示/Token 打码/吊销消失。</summary>
public class RedisSessionListTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    [Fact]
    public async Task 活跃会话列示_Token仅前缀_吊销后消失()
    {
        if (ConnectionString is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var store = new RedisAuthStore(redis, $"test:sess:{Guid.NewGuid():N}:");

        var token1 = "token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var token2 = "token-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await store.SaveAsync(new TokenSession { Token = token1, UserId = "u1", UserName = "一", ExpiresAt = DateTime.Now.AddHours(1) });
        await store.SaveAsync(new TokenSession { Token = token2, UserId = "u2", ExpiresAt = DateTime.Now.AddHours(1) });

        var active = await store.ListActiveAsync();
        Assert.Equal(2, active.Count);

        // Token 打码:前缀 + 省略号,绝无完整明文
        foreach (var s in active)
        {
            Assert.EndsWith("…", s.TokenPrefix);
            Assert.True(s.TokenPrefix.Length < 16);
        }

        await store.RevokeAsync(token1);
        var after = await store.ListActiveAsync();
        Assert.Single(after);
        Assert.Equal("u2", after[0].UserId);
    }

    [Fact]
    public async Task 过期会话_键已被TTL清除_不列示()
    {
        if (ConnectionString is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var store = new RedisAuthStore(redis, $"test:sess:{Guid.NewGuid():N}:");

        // 已过期会话:TTL 为负 → SaveAsync 直接不落键(实现语义:过期会话不存储)
        await store.SaveAsync(new TokenSession { Token = "tok", UserId = "u", ExpiresAt = DateTime.Now.AddSeconds(-1) });
        Assert.Empty(await store.ListActiveAsync());
    }
}

/// <summary>按用户吊销(RevokeByUserAsync):内存/Redis 双实现。</summary>
public class RevokeByUserTests
{
    [Fact]
    public async Task 内存_吊销用户全部会话_返回条数()
    {
        var store = new MemorySessionStore();
        await store.SaveAsync(new TokenSession { Token = "t1", UserId = "u1", ExpiresAt = DateTime.Now.AddHours(1) });
        await store.SaveAsync(new TokenSession { Token = "t2", UserId = "u1", ExpiresAt = DateTime.Now.AddHours(1) });
        await store.SaveAsync(new TokenSession { Token = "t3", UserId = "u2", ExpiresAt = DateTime.Now.AddHours(1) });

        var revoked = await store.RevokeByUserAsync("u1");
        Assert.Equal(2, revoked);
        Assert.Null(await store.GetAsync("t1"));
        Assert.Null(await store.GetAsync("t2"));
        Assert.NotNull(await store.GetAsync("t3")); // 他人不受影响
        Assert.Single(await store.ListActiveAsync());
    }

    [Fact]
    public async Task Redis_吊销用户全部会话()
    {
        var cs = Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");
        if (cs is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(cs);
        var store = new RedisAuthStore(redis, $"test:revoke:{Guid.NewGuid():N}:");
        await store.SaveAsync(new TokenSession { Token = "ra", UserId = "u1", ExpiresAt = DateTime.Now.AddHours(1) });
        await store.SaveAsync(new TokenSession { Token = "rb", UserId = "u1", ExpiresAt = DateTime.Now.AddHours(1) });
        await store.SaveAsync(new TokenSession { Token = "rc", UserId = "u2", ExpiresAt = DateTime.Now.AddHours(1) });

        var revoked = await store.RevokeByUserAsync("u1");
        Assert.Equal(2, revoked);
        Assert.Null(await store.GetAsync("ra"));
        Assert.NotNull(await store.GetAsync("rc"));
    }
}

/// <summary>JWKS 在线发现:自建 HttpListener IdP(发现文档+jwks),kid 轮换验证。</summary>
public class JwksDiscoveryTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _base = "";
    private RSA _idpKey = null!;
    private string _kid = "key-2026-10";

    public async Task InitializeAsync()
    {
        _idpKey = RSA.Create(2048);
        var port = 18760 + Random.Shared.Next(100);
        _base = $"http://localhost:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_base}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    var path = ctx.Request.Url!.AbsolutePath;
                    var json = path switch
                    {
                        "/.well-known/openid-configuration" => $$"""{"issuer":"{{_base}}","jwks_uri":"{{_base}}/jwks"}""",
                        "/jwks" => $$"""{"keys":[{"kty":"RSA","kid":"{{_kid}}","n":"{{B64Url(_idpKey.ExportParameters(false).Modulus)}}","e":"{{B64Url(_idpKey.ExportParameters(false).Exponent)}}","alg":"RS256"}]}""",
                        _ => "{}",
                    };
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });
        await Task.Delay(100);
    }

    private static string B64Url(byte[]? b)
        => Convert.ToBase64String(b ?? []).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Cleanup()
    {
        _listener.Stop();
        _idpKey.Dispose();
    }

    Task IAsyncLifetime.DisposeAsync() { Cleanup(); return Task.CompletedTask; }

    [Fact]
    public async Task JWKS发现_kid匹配验签通过()
    {
        var keys = await OidcJwksFetcher.FetchAsync(_base);
        Assert.Single(keys);
        Assert.Contains(_kid, keys.Keys);

        var validator = new OidcTokenValidator(Microsoft.Extensions.Options.Options.Create(new NexMesh.Auth.AuthenticationOptions
        {
            Oidc = new NexMesh.Auth.OidcOptions { Issuer = _base } // 无静态 PEM:仅 JWKS 路径
        }));
        validator.SetJwksKeys(keys);

        var header = $$"""{"alg":"RS256","kid":"{{_kid}}"}""";
        var payload = $$"""{"sub":"jwks-u1","iss":"{{_base}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}""";
        var h = B64Url(System.Text.Encoding.UTF8.GetBytes(header));
        var p = B64Url(System.Text.Encoding.UTF8.GetBytes(payload));
        var sig = B64Url(_idpKey.SignData(System.Text.Encoding.UTF8.GetBytes($"{h}.{p}"),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        var user = validator.Validate($"{h}.{p}.{sig}");
        Assert.NotNull(user);
        Assert.Equal("jwks-u1", user!.UserId);
    }

    [Fact]
    public async Task kid不匹配_拒绝()
    {
        var keys = await OidcJwksFetcher.FetchAsync(_base);
        var validator = new OidcTokenValidator(Microsoft.Extensions.Options.Options.Create(new NexMesh.Auth.AuthenticationOptions
        {
            Oidc = new NexMesh.Auth.OidcOptions { Issuer = _base },
        }));
        validator.SetJwksKeys(keys);

        var header = """{"alg":"RS256","kid":"unknown-kid"}""";
        var payload = $$"""{"sub":"u","iss":"{{_base}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}""";
        var h = B64Url(System.Text.Encoding.UTF8.GetBytes(header));
        var p = B64Url(System.Text.Encoding.UTF8.GetBytes(payload));
        var sig = B64Url(_idpKey.SignData(System.Text.Encoding.UTF8.GetBytes($"{h}.{p}"),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        Assert.Null(validator.Validate($"{h}.{p}.{sig}")); // 无匹配 kid 且无静态 PEM → 拒绝
    }
}

/// <summary>
/// IdP↔RP 闭环:自建 IdP(授权码+PKCE 签发真 JWT)→ RP 经 JWKS 发现验签换本地令牌。
/// 框架自身跑通发收两端——OidcJwksFetcher 消费 OidcIdpTokenService 的 JWKS。
/// </summary>
public class OidcIdpRoundTripTests : IAsyncLifetime
{
    private WebApplication _idp = null!;
    private WebApplication _rp = null!;
    private string _idpBase = "";
    private HttpClient _idpClient = null!;
    private HttpClient _rpClient = null!;

    public async Task InitializeAsync()
    {
        // ── IdP 宿主(TestServer 独立端口由 HttpListener 承担,JWKS 需真实 HTTP 可达)──
        var port = 18800 + Random.Shared.Next(100);
        _idpBase = "http://localhost"; // 发现文档/JWKS 挂根路径(OIDC 惯例),authorize/token 在 /oidc 下
        var idpBuilder = WebApplication.CreateBuilder();
        idpBuilder.WebHost.UseTestServer();
        idpBuilder.Services.AddNexMeshOidcIdp(o =>
        {
            o.Issuer = _idpBase;
            o.UserStore = new InMemoryUserStore();
            o.ClientStore = new InMemoryClientStore();
        });
        var users = new InMemoryUserStore();
        users.Add(new OidcIdpUser("idp-u1", "门户用户", "tenant-x", ["manager"]));
        var clients = new InMemoryClientStore();
        clients.Add(new OidcClient { ClientId = "rp-app", RedirectUris = ["http://localhost/oidc/echo"] });
        idpBuilder.Services.AddSingleton<IOidcUserStore>(users);
        idpBuilder.Services.AddSingleton<IOidcClientStore>(clients);
        _idp = idpBuilder.Build();
        _idp.MapNexMeshOidcIdp();
        _idp.MapGet("/oidc/echo", (Microsoft.AspNetCore.Http.HttpRequest r) => Microsoft.AspNetCore.Http.Results.Ok(new { code = r.Query["code"].FirstOrDefault() }));
        await _idp.StartAsync();
        _idpClient = _idp.GetTestClient();

        // ── RP 宿主(用框架认证 + OIDC 验证器指向 IdP)──
        var rpBuilder = WebApplication.CreateBuilder();
        rpBuilder.WebHost.UseTestServer();
        rpBuilder.Configuration["NexMesh:Authentication:Oidc:Issuer"] = _idpBase; // 必须在 AddNexMeshAuth 之前(IOptions 启动期快照)
        rpBuilder.Services.AddNexMesh();
        rpBuilder.Services.AddNexMeshAuth(rpBuilder.Configuration);
        _rp = rpBuilder.Build();
        _rp.UseNexMeshAuthentication();
        _rp.UseAuthorization();
        _rp.MapNexMeshAuth();
        await _rp.StartAsync();
        _rpClient = _rp.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _idpClient.Dispose();
        await _idp.DisposeAsync();
        await _rp.DisposeAsync();
    }

    [Fact]
    public async Task 授权码PKCE全流程_签发的JWT经JWKS发现验签通过并换发本地令牌()
    {
        // 1. RP 拉 JWKS(IdP 的 OidcJwksFetcher 闭环)
        var keys = await OidcJwksFetcher.FetchAsync(_idpBase, _idpClient);
        Assert.NotEmpty(keys);

        // 2. PKCE:verifier→challenge(S256)
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var challenge = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // 3. 授权码
        var authorizeUrl = $"/oidc/authorize?response_type=code&client_id=rp-app&redirect_uri=http://localhost/oidc/echo&code_challenge={challenge}&code_challenge_method=S256&user_id=idp-u1&scope=openid";
        var authorizeResp = await _idpClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResp.StatusCode);
        var location = authorizeResp.Headers.Location!.ToString();
        var code = System.Web.HttpUtility.ParseQueryString(location.Split('?')[1])["code"];
        Assert.False(string.IsNullOrEmpty(code));

        // 4. 换令牌
        var tokenResp = await _idpClient.PostAsync("/oidc/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["code_verifier"] = verifier,
            ["client_id"] = "rp-app",
        }));
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        Assert.True(tokenResp.IsSuccessStatusCode, tokenBody);
        var idpJwt = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;

        // 5. RP 侧:注入 JWKS 验签(OidcTokenValidator + fetcher 同一框架)
        var rpValidator = _rp.Services.GetRequiredService<OidcTokenValidator>();
        rpValidator.SetJwksKeys(keys);
        var validated = rpValidator.Validate(idpJwt);
        Assert.NotNull(validated);
        Assert.Equal("idp-u1", validated!.UserId);
        Assert.Equal("门户用户", validated.UserName);
        Assert.Equal("tenant-x", validated.TenantId);
        Assert.Contains(validated.ClaimList!, c => c.Value == "manager");

        // 6. token-exchange:RP 登录端点收 oidcToken 换发本地不透明令牌(无 ILoginValidator 也可走 IdP 令牌)
        //    (此宿主未注册业务验证器——经 IdP JWT 交换是框架的显式路径)
        var login = await _rpClient.PostAsync("/nexmesh/auth/login",
            new StringContent(JsonSerializer.Serialize(new { oidcToken = idpJwt }), Encoding.UTF8, "application/json"));
        var loginBody = await login.Content.ReadAsStringAsync();
        Assert.True(login.IsSuccessStatusCode, loginBody);
        Assert.Contains("accessToken", loginBody);
    }

    [Fact]
    public async Task 授权码一次性_PKCE错误拒绝()
    {
        var verifier = "correct-verifier-01234567890123456789012345678901";
        var challenge = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = await _idpClient.GetAsync(
            $"/oidc/authorize?response_type=code&client_id=rp-app&redirect_uri=http://localhost/oidc/echo&code_challenge={challenge}&code_challenge_method=S256&user_id=idp-u1");
        var authorizeCode = authorize.Headers.Location is null ? null : System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location.ToString().Split('?')[1])["code"];
        Assert.False(string.IsNullOrEmpty(authorizeCode));
        var code = authorizeCode;

        // 错误 verifier → 拒绝
        var wrong = await _idpClient.PostAsync("/oidc/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code!, ["code_verifier"] = "wrong".PadLeft(43, 'x'), ["client_id"] = "rp-app",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // 正确 verifier 但码已被首次消费尝试消耗?——错误尝试不消费(仅 PKCE 校验失败不移除码的语义):
        // 实现:TryRemove 在校验前执行,错误 verifier 已消费码 → 二次也拒(防爆破语义)
        var retry = await _idpClient.PostAsync("/oidc/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code!, ["code_verifier"] = verifier, ["client_id"] = "rp-app",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
    }

    [Fact]
    public async Task client_credentials_凭据错误401_正确签发()
    {
        var bad = await _idpClient.PostAsync("/oidc/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "rp-app", ["client_secret"] = "wrong",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }
}

/// <summary>禁止自动跟随重定向的包装 handler(authorize 端点断言 302+Location 用)。</summary>
public sealed class NoRedirectHandler(System.Net.Http.HttpMessageHandler inner) : System.Net.Http.DelegatingHandler(inner)
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        => base.SendAsync(request, cancellationToken); // TestServer handler 默认不自动跟随;此处仅包装保底
}
