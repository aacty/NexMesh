namespace NexMesh.Auth;

using System.Security.Claims;

/// <summary>
/// Token 签发服务(不透明令牌实现):GenerateAccessToken 签发可吊销的不透明访问令牌并落会话;
/// GenerateRefreshToken 签发随机刷新令牌;GetPrincipalFromExpiredToken 经声明日志
/// 重建过期令牌的 ClaimsPrincipal(刷新流程用);ValidateToken 校验令牌当前有效。
/// </summary>
public interface IJwtTokenService
{
    string GenerateAccessToken(IEnumerable<Claim> claims);

    RefreshTokenInfo GenerateRefreshToken(string userId);

    ClaimsPrincipal? GetPrincipalFromExpiredToken(string accessToken);

    bool ValidateToken(string accessToken);
}

public static class JwtTokenServiceExtensions
{
    /// <summary>按约定把业务用户模型映射为声明并签发访问令牌。</summary>
    public static string GenerateAccessToken<TUser>(this IJwtTokenService jwtTokenService, TUser user)
        => jwtTokenService.GenerateAccessToken(NexMesh.Core.UserClaimsMapper.ToClaims(user));
}

/// <summary>刷新令牌信息。</summary>
public sealed class RefreshTokenInfo
{
    public string Token { get; set; } = "";

    public string UserId { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsExpired => DateTimeOffset.Now >= ExpiresAt;
}

/// <summary>
/// 令牌白名单存储(业务登录/刷新/换角色/登出用):
/// Store* 登记令牌;Validate* 校验令牌是否仍有效;Remove* 吊销单令牌;RemoveAll 全端登出。
/// 注册后与认证管线联动——RemoveAccessTokenAsync 会真实吊销会话(立即失效)。
/// </summary>
public interface ITokenStore
{
    Task StoreAccessTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default);

    Task StoreRefreshTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default);

    Task<bool> ValidateAccessTokenAsync(string userId, string token, CancellationToken ct = default);

    Task<bool> ValidateRefreshTokenAsync(string userId, string token, CancellationToken ct = default);

    Task RemoveAccessTokenAsync(string userId, string token, CancellationToken ct = default);

    Task RemoveRefreshTokenAsync(string userId, string token, CancellationToken ct = default);

    Task RemoveAllTokensAsync(string userId, CancellationToken ct = default);
}

/// <summary>认证配置节(NexMesh:Authentication)。</summary>
public sealed class AuthenticationOptions
{
    public JwtOptions Jwt { get; set; } = new();

    /// <summary>外部 OIDC IdP 验证(配置 Issuer+密钥即启用;token-exchange 换发本地令牌)。</summary>
    public OidcOptions? Oidc { get; set; }

    public ApiKeyOptions ApiKey { get; set; } = new();
}

/// <summary>JWT 配置(签发参数沿用既有命名,底层为不透明令牌)。</summary>
public sealed class JwtOptions
{
    public bool Enabled { get; set; } = true;

    public string Secret { get; set; } = "";

    public string? MetadataAddress { get; set; }

    public string Issuer { get; set; } = "";

    public string Audience { get; set; } = "";

    public int AccessTokenExpirationMinutes { get; set; } = 1440;

    public int RefreshTokenExpirationDays { get; set; } = 7;

    public string SigningAlgorithm { get; set; } = "";

    public bool ValidateIssuer { get; set; }

    public bool ValidateAudience { get; set; }

    public bool ValidateLifetime { get; set; } = true;

    public int ClockSkewSeconds { get; set; }
}

/// <summary>API Key 认证配置(默认关闭)。</summary>
public sealed class ApiKeyOptions
{
    public bool Enabled { get; set; }

    public string HeaderName { get; set; } = "X-Api-Key";

    public Dictionary<string, string> ApiKeys { get; set; } = [];
}
