namespace NexMesh.Auth.Oidc;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using System.Security.Cryptography;

/// <summary>
/// 自研轻量 OIDC IdP(身份提供方):让 NexMesh 服务能当"被别人对接的身份中心"。
/// 与 OpenIddict/SK 同判:镜像源不可用 + 包铁律 → 零依赖自实现核心子集:
///   授权码 + PKCE(公共客户端)/ client 凭据 / 发现文档 / JWKS 端点(密钥轮换)。
/// 令牌为真 JWT(RS256, kid 标头),与 OidcTokenValidator+OidcJwksFetcher 组成闭环:
/// IdP 签发 → RP 经 JWKS 发现验签——框架自身跑通"发的一方"和"收的一方"。
/// 不做:implicit(已废)、刷新令牌滚动(留扩展)、client secret JWT 签名(留扩展)。
/// </summary>
public static class NexMeshOidcIdpExtensions
{
    /// <summary>注册 IdP(须在 AddNexMeshAuth 之前/之后均可,端点独立)。</summary>
    public static IServiceCollection AddNexMeshOidcIdp(
        this IServiceCollection services,
        Action<OidcIdpOptions> configure)
    {
        var options = new OidcIdpOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton<OidcIdpTokenService>();
        services.AddSingleton<IOidcUserStore>(options.UserStore ?? new InMemoryUserStore());
        services.AddSingleton<IOidcClientStore>(options.ClientStore ?? new InMemoryClientStore());
        return services;
    }

    /// <summary>
    /// 挂载 IdP 端点(路径前缀默认 /oidc,建议挂认证之后由业务决定哪些要登录):
    ///   GET  /.well-known/openid-configuration(发现文档)
    ///   GET  /jwks(JWKS 公钥集,kid 标识)
    ///   GET  /authorize(授权码端点,PKCE S256 必须)
    ///   POST /token(授权码换令牌 / client_credentials)
    /// </summary>
    public static IEndpointRouteBuilder MapNexMeshOidcIdp(this IEndpointRouteBuilder endpoints, string prefix = "/oidc")
    {
        prefix = "/" + prefix.Trim('/');
        var sp = endpoints.ServiceProvider;
        var options = sp.GetRequiredService<OidcIdpOptions>();
        var tokens = sp.GetRequiredService<OidcIdpTokenService>();
        var users = sp.GetRequiredService<IOidcUserStore>();
        var clients = sp.GetRequiredService<IOidcClientStore>();

        // 发现文档(RP 的 OidcJwksFetcher 消费同一结构)
        // 发现文档与 JWKS 挂根路径(OIDC 惯例:发现 URL = {issuer}/.well-known/openid-configuration,与 OidcJwksFetcher 拼法对齐)
        endpoints.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = options.Issuer,
            jwks_uri = options.Issuer.TrimEnd('/') + "/jwks",
            authorization_endpoint = options.Issuer.TrimEnd('/') + prefix + "/authorize",
            token_endpoint = options.Issuer.TrimEnd('/') + prefix + "/token",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "client_credentials" },
            code_challenge_methods_supported = new[] { "S256" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));

        // JWKS(公钥集;轮换=TokenService 重建密钥后此端点自动吐新 kid)
        endpoints.MapGet("/jwks", () => Results.Json(new
        {
            keys = new object[] { tokens.ExportPublicJwk() },
        }));

        // 授权码端点:PKCE 必须(S256);user_id 经登录会话传(业务登录后重定向回来带 user_id 参数——简化凭据传递)
        endpoints.MapGet($"{prefix}/authorize", async (HttpRequest req) =>
        {
            var q = req.Query;
            string? Val(string k) => q[k].FirstOrDefault();
            var clientId = Val("client_id");
            var redirectUri = Val("redirect_uri");
            var codeChallenge = Val("code_challenge");
            var userId = Val("user_id");

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri)
                || string.IsNullOrEmpty(codeChallenge))
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }
            var client = await clients.FindAsync(clientId);
            if (client is null || !client.RedirectUris.Contains(redirectUri))
            {
                return Results.BadRequest(new { error = "invalid_client" });
            }
            if (Val("code_challenge_method") != "S256")
            {
                return Results.BadRequest(new { error = "invalid_request", error_description = "仅支持 S256" });
            }
            if (string.IsNullOrEmpty(userId) || await users.FindAsync(userId) is null)
            {
                return Results.BadRequest(new { error = "access_denied", error_description = "未登录(user_id 缺失或无效)" });
            }

            var code = tokens.IssueAuthorizationCode(clientId, redirectUri, codeChallenge, userId);
            var sep = redirectUri.Contains('?') ? '&' : '?';
            return Results.Redirect($"{redirectUri}{sep}code={Uri.EscapeDataString(code)}");
        });

        // 令牌端点:授权码(验 PKCE)换 JWT / client_credentials
        endpoints.MapPost($"{prefix}/token", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            string? Val(string k) => form[k].FirstOrDefault();
            var grantType = Val("grant_type");

            if (grantType == "authorization_code")
            {
                var code = Val("code");
                var verifier = Val("code_verifier");
                var clientId = Val("client_id");
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier))
                {
                    return Results.Json(new { error = "invalid_request" }, statusCode: 400);
                }
                var userId = tokens.RedeemAuthorizationCode(code!, verifier!, clientId!);
                if (userId is null || await users.FindAsync(userId) is not { } user)
                {
                    return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                }
                var jwt = tokens.IssueUserToken(user, clientId!);
                return Results.Json(new { access_token = jwt, token_type = "Bearer", expires_in = 3600 });
            }
            if (grantType == "client_credentials")
            {
                var clientId = Val("client_id");
                var clientSecret = Val("client_secret");
                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    return Results.Json(new { error = "invalid_request" }, statusCode: 400);
                }
                var client = await clients.FindAsync(clientId);
                if (client?.ClientSecret != clientSecret)
                {
                    return Results.Json(new { error = "invalid_client" }, statusCode: 401);
                }
                var jwt = tokens.IssueClientCredentials(clientId);
                return Results.Json(new { access_token = jwt, token_type = "Bearer", expires_in = 3600 });
            }
            return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
        });

        return endpoints;
    }
}

/// <summary>IdP 配置。</summary>
public sealed class OidcIdpOptions
{
    /// <summary>本 IdP 的签发方标识(RP 侧 OidcOptions.Issuer 填同一个)。</summary>
    public string Issuer { get; set; } = "http://localhost";

    /// <summary>用户存储(登录后的用户;缺省内存——生产注入实现)。</summary>
    public IOidcUserStore? UserStore { get; set; }

    /// <summary>客户端注册(RP 清单;缺省内存)。</summary>
    public IOidcClientStore? ClientStore { get; set; }
}

/// <summary>IdP 用户存储。</summary>
public interface IOidcUserStore
{
    Task<OidcIdpUser?> FindAsync(string userId);
}

/// <summary>IdP 用户(声明来源)。</summary>
public sealed record OidcIdpUser(
    string UserId,
    string? UserName = null,
    string? TenantId = null,
    IReadOnlyList<string>? Roles = null);

/// <summary>IdP 客户端(RP)注册。</summary>
public interface IOidcClientStore
{
    Task<OidcClient?> FindAsync(string clientId);
}

/// <summary>RP 客户端定义。</summary>
public sealed record OidcClient
{
    public string ClientId { get; init; } = "";
    public string? ClientSecret { get; init; }

    /// <summary>允许的回调地址(authorize 校验)。</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];
}

/// <summary>内存实现(开发/测试/单租户小规模)。</summary>
public sealed class InMemoryUserStore : IOidcUserStore
{
    private readonly Dictionary<string, OidcIdpUser> _users = new(StringComparer.Ordinal);
    public void Add(OidcIdpUser user) => _users[user.UserId] = user;
    public Task<OidcIdpUser?> FindAsync(string userId)
        => Task.FromResult(_users.TryGetValue(userId, out var u) ? u : null);
}

public sealed class InMemoryClientStore : IOidcClientStore
{
    private readonly Dictionary<string, OidcClient> _clients = new(StringComparer.Ordinal);
    public void Add(OidcClient client) => _clients[client.ClientId] = client;
    public Task<OidcClient?> FindAsync(string clientId)
        => Task.FromResult(_clients.TryGetValue(clientId, out var c) ? c : null);
}

/// <summary>
/// IdP 令牌服务:RS256 密钥对(启动生成;轮换=RotateKey 重建,kid 随之更换)、
/// JWT 签发(sub/preferred_username/tenantid/roles → 与 RP 侧 OidcTokenValidator 的声明映射对齐)、
/// 授权码签发与 PKCE 兑换(一次性,90 秒)。
/// </summary>
public sealed class OidcIdpTokenService(OidcIdpOptions options)
{
    private readonly string _issuerHolder = options.Issuer;
    private RSA _key = RSA.Create(2048);
    private string _kid = $"nexmesh-idp-{Guid.CreateVersion7():N}";
    private readonly ConcurrentDictionary<string, (string ClientId, string RedirectUri, string Challenge, string UserId, DateTimeOffset ExpiresAt)> _codes = new();

    /// <summary>密钥轮换:重建密钥对(新 kid);已签发令牌在 RP 拉取新 JWKS 前仍可由旧密钥验证失败——生产轮换应双 kid 过渡,此处简化为单 key 即时切换。</summary>
    public void RotateKey()
    {
        _key.Dispose();
        _key = RSA.Create(2048);
        _kid = $"nexmesh-idp-{Guid.CreateVersion7():N}";
    }

    /// <summary>导出公钥为 JWKS 键对象(n/e Base64Url + kid)。</summary>
    public object ExportPublicJwk()
    {
        var p = _key.ExportParameters(false);
        return new
        {
            kty = "RSA",
            kid = _kid,
            use = "sig",
            alg = "RS256",
            n = B64Url(p.Modulus),
            e = B64Url(p.Exponent),
        };
    }

    /// <summary>签发用户 JWT(声明映射与 RP 验证器约定一致)。</summary>
    public string IssueUserToken(OidcIdpUser user, string clientId)
    {
        var header = $$"""{"alg":"RS256","kid":"{{_kid}}","typ":"JWT"}""";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sub"] = user.UserId,
            ["preferred_username"] = user.UserName,
            ["tenantid"] = user.TenantId,
            ["roles"] = user.Roles ?? [],
            ["iss"] = _issuerHolder,
            ["aud"] = clientId,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        var h = B64Url(System.Text.Encoding.UTF8.GetBytes(header));
        var p = B64Url(System.Text.Encoding.UTF8.GetBytes(payload));
        var sig = B64Url(_key.SignData(System.Text.Encoding.UTF8.GetBytes($"{h}.{p}"),
            System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return $"{h}.{p}.{sig}";
    }

    /// <summary>签发 client_credentials JWT。</summary>
    public string IssueClientCredentials(string clientId) => IssueUserToken(new OidcIdpUser(clientId), clientId);

    /// <summary>签发授权码(一次性,90 秒)。</summary>
    public string IssueAuthorizationCode(string clientId, string redirectUri, string challenge, string userId)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _codes[code] = (clientId, redirectUri, challenge, userId, DateTimeOffset.UtcNow.AddSeconds(90));
        return code;
    }

    /// <summary>兑换授权码:PKCE S256 校验 + 一次性消费;成功返回 user_id(端点查存储后签发完整声明)。</summary>
    public string? RedeemAuthorizationCode(string code, string verifier, string clientId)
    {
        if (!_codes.TryRemove(code, out var entry))
        {
            return null; // 不存在或已用(一次性)
        }
        if (entry.ExpiresAt < DateTimeOffset.UtcNow || entry.ClientId != clientId)
        {
            return null;
        }
        var verifierHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        if (!string.Equals(B64Url(verifierHash), entry.Challenge, StringComparison.Ordinal))
        {
            return null; // PKCE 不匹配
        }
        return entry.UserId;
    }


    private static string B64Url(byte[]? b)
        => Convert.ToBase64String(b ?? []).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
