namespace NexMesh.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

/// <summary>
/// IJwtTokenService 不透明令牌实现:
/// GenerateAccessToken 签发可吊销访问令牌(会话 + 声明日志双落,多值声明以 ClaimList 承载,
/// 认证处理器逐条展开为 Claims——RequireRole/IsInRole 语义完整);
/// GetPrincipalFromExpiredToken 经声明日志重建过期令牌身份(刷新流程);
/// ValidateToken 校验令牌当前会话有效。
/// 同步签名与业务调用形态一致;内部阻塞执行存储写(单次请求内 1-2 次,可忽略)。
/// </summary>
public sealed class NexMeshJwtTokenService(
    NexMeshTokenService tokens,
    IClaimsJournal journal,
    IOptions<AuthenticationOptions> options) : IJwtTokenService
{
    private readonly AuthenticationOptions _options = options.Value;

    public string GenerateAccessToken(IEnumerable<Claim> claims)
    {
        var list = claims.ToList();
        var userId = list.FirstOrDefault(c => c.Type is ClaimTypes.NameIdentifier or "sub")?.Value ?? "";
        var userName = list.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;
        // 单值字典(旧兼容面)+ 多值列表(权威面)同时落会话
        var dict = list.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value);
        var session = tokens.IssueAsync(new LoginUser(userId, userName, null, dict)
        {
            ClaimList = list.Select(c => new TokenClaim(c.Type, c.Value)).ToList(),
        }).GetAwaiter().GetResult();

        // 声明日志:存活至刷新令牌有效期,过期令牌仍可重建身份
        journal.SetClaimsAsync(
            session.Token, list, TimeSpan.FromDays(_options.Jwt.RefreshTokenExpirationDays)).GetAwaiter().GetResult();
        return session.Token;
    }

    public RefreshTokenInfo GenerateRefreshToken(string userId)
    {
        var info = new RefreshTokenInfo
        {
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            UserId = userId,
            ExpiresAt = DateTimeOffset.Now.AddDays(_options.Jwt.RefreshTokenExpirationDays),
        };
        journal.AddRefreshAsync(
            userId, info.Token, TimeSpan.FromDays(_options.Jwt.RefreshTokenExpirationDays)).GetAwaiter().GetResult();
        return info;
    }

    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string accessToken)
    {
        var claims = journal.GetClaimsAsync(accessToken).GetAwaiter().GetResult();
        return claims is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(claims, "nexmesh-refresh-grace"));
    }

    public bool ValidateToken(string accessToken) =>
        tokens.GetAsync(accessToken).GetAwaiter().GetResult() is not null;
}
