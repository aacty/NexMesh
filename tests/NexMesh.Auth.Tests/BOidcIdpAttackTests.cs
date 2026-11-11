namespace NexMesh.Auth.Tests;

using System.Net;
using System.Web;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using NexMesh.Auth.Oidc;
using Xunit;

/// <summary>
/// 攻击面 2(框架架构师 B 对抗测试):自研 OIDC IdP authorize/token 端点语义锁定与风险实证。
/// 已知简化(文档声明):user_id 经 query 直传即可领码 —— 本文件用端到端攻击链实证其实际风险:
/// 攻击者仅需一个可猜测的 user_id(无密码/无会话/无凭据)即可换取该用户完整 JWT。
/// 同时锁定防御面语义:code_challenge 缺失拒绝、method=plain 拒绝(仅 S256)、
/// redirect_uri 精确白名单、token 端点对 grant_type 模糊输入一律 400 unsupported_grant_type。
/// </summary>
public class BOidcIdpAttackTests : IAsyncLifetime
{
    private const string Verifier = "b-attack-verifier-012345678901234567890123456789012";

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var users = new InMemoryUserStore();
        users.Add(new OidcIdpUser("alice", "爱丽丝", "tenant-a", ["admin"]));
        var clients = new InMemoryClientStore();
        clients.Add(new OidcClient { ClientId = "rp-app", RedirectUris = ["https://rp.test/cb"] });

        builder.Services.AddNexMeshOidcIdp(o =>
        {
            o.Issuer = "http://localhost";
            o.UserStore = users;
            o.ClientStore = clients;
        });

        _app = builder.Build();
        _app.MapNexMeshOidcIdp();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>标准 S256 challenge = base64url(SHA256(ASCII(verifier)))。</summary>
    private static string S256Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(HttpStatusCode Status, string Body, string? Location)> GetAsync(string url)
    {
        var response = await _http.GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task authorize_缺code_challenge_400invalid_request()
    {
        var (status, body, _) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb&user_id=alice");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("invalid_request", body);
    }

    [Theory]
    [InlineData("plain")]   // 显式声明 plain
    [InlineData("")]        // 不带 method 参数(query 拼接为空值)
    public async Task authorize_code_challenge_method非S256_一律400仅支持S256(string method)
    {
        var methodParam = method.Length == 0 ? "" : $"&code_challenge_method={method}";
        var (status, body, _) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}{methodParam}&user_id=alice");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("S256", body);
    }

    [Fact]
    public async Task authorize_redirect_uri不在白名单_400invalid_client_防开放重定向()
    {
        var (status, body, _) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Fevil.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}&code_challenge_method=S256&user_id=alice");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("invalid_client", body);
    }

    [Fact]
    public async Task authorize_user_id无效_400access_denied_语义锁定()
    {
        var (status, body, _) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}&code_challenge_method=S256&user_id=ghost-user");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("access_denied", body);
    }

    [Fact]
    public async Task authorize_user_id缺失_400access_denied()
    {
        var (status, body, _) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("access_denied", body);
    }

    [Fact]
    public async Task 风险实证_仅凭可猜测的user_id_无凭据领码_端到端换取受害者JWT()
    {
        // 第一步:authorize 端点 user_id=alice 直传(零凭据/零会话)→ 302 带 code
        var (status, _, location) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}&code_challenge_method=S256&user_id=alice");
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.NotNull(location);
        Assert.StartsWith("https://rp.test/cb?code=", location);

        // 第二步:code + PKCE verifier 换 JWT(全程未提交任何密码)
        var code = ExtractCode(location);
        Assert.False(string.IsNullOrEmpty(code));
        var tokenResponse = await _http.PostAsync("/oidc/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["code_verifier"] = Verifier,
                ["client_id"] = "rp-app",
            }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await tokenResponse.Content.ReadAsStringAsync());
        var jwt = json.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrEmpty(jwt));

        // 第三步:验证拿到的是 alice 的完整身份 JWT(含角色)
        var payload = JsonSerializer.Deserialize<JsonElement>(
            System.Text.Encoding.UTF8.GetString(Base64UrlDecode(jwt!.Split('.')[1])));
        Assert.Equal("alice", payload.GetProperty("sub").GetString());
        Assert.Contains("admin", payload.GetProperty("roles").GetRawText());
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padding = (4 - value.Length % 4) % 4;
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', padding));
    }

    /// <summary>从 302 Location(https://rp.test/cb?code=XXX)中提取授权码。</summary>
    private static string ExtractCode(string location)
        => location[(location.IndexOf("code=", StringComparison.Ordinal) + "code=".Length)..];

    [Theory]
    [InlineData("password")]            // 伪造 ROPC
    [InlineData("client_credentials ")] // 带尾随空格
    [InlineData("AUTHORIZATION_CODE")]  // 大小写不宽容
    [InlineData("")]                    // 缺 grant_type
    public async Task token_grant_type模糊输入_一律400unsupported_grant_type(string grantType)
    {
        var form = new Dictionary<string, string>();
        if (grantType.Length > 0)
        {
            form["grant_type"] = grantType;
        }
        var response = await _http.PostAsync("/oidc/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unsupported_grant_type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task token_授权码兑换_verifier错误_400invalid_grant()
    {
        // 先合法领一个码
        var (_, _, location) = await GetAsync(
            "/oidc/authorize?client_id=rp-app&redirect_uri=https%3A%2F%2Frp.test%2Fcb" +
            $"&code_challenge={S256Challenge(Verifier)}&code_challenge_method=S256&user_id=alice");
        var code = ExtractCode(location!);

        var response = await _http.PostAsync("/oidc/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["code_verifier"] = Verifier + "x", // PKCE 不匹配
                ["client_id"] = "rp-app",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task token_缺code或verifier_400invalid_request()
    {
        var missingVerifier = await _http.PostAsync("/oidc/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "whatever",
                ["client_id"] = "rp-app",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, missingVerifier.StatusCode);
        Assert.Contains("invalid_request", await missingVerifier.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task token_client_credentials错secret_401invalid_client_常量时间比较面()
    {
        var response = await _http.PostAsync("/oidc/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "rp-app",
                ["client_secret"] = "wrong-secret",
            }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_client", await response.Content.ReadAsStringAsync());
    }
}
