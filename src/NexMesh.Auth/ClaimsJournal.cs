namespace NexMesh.Auth;

using System.Collections.Concurrent;
using System.Security.Claims;

/// <summary>
/// 声明日志:访问令牌签发时把声明快照独立留存(存活期 = 刷新令牌有效期),
/// 令牌过期后仍可重建 ClaimsPrincipal(刷新流程 GetPrincipalFromExpiredToken 用);
/// 刷新令牌白名单(userId+token 维度校验)也托管于此。
/// </summary>
public interface IClaimsJournal
{
    Task SetClaimsAsync(string accessToken, IReadOnlyList<Claim> claims, TimeSpan lifetime, CancellationToken ct = default);

    Task<IReadOnlyList<Claim>?> GetClaimsAsync(string accessToken, CancellationToken ct = default);

    Task RemoveClaimsAsync(string accessToken, CancellationToken ct = default);

    Task AddRefreshAsync(string userId, string refreshToken, TimeSpan lifetime, CancellationToken ct = default);

    Task<bool> HasRefreshAsync(string userId, string refreshToken, CancellationToken ct = default);

    Task RemoveRefreshAsync(string userId, string refreshToken, CancellationToken ct = default);

    /// <summary>移除用户全部令牌相关条目(全端登出)。</summary>
    Task RemoveAllUserAsync(string userId, CancellationToken ct = default);
}

/// <summary>声明条目序列化形状(Claim 属性只读不可反序列化,经此中转)。</summary>
public sealed record JournalClaim(string Type, string Value)
{
    public Claim ToClaim() => new(Type, Value);

    public static JournalClaim From(Claim claim) => new(claim.Type, claim.Value);
}

/// <summary>内存实现(单实例部署/开发/测试):带惰性清扫,防字典无界增长。</summary>
public sealed class MemoryClaimsJournal : IClaimsJournal
{
    private readonly ConcurrentDictionary<string, (IReadOnlyList<JournalClaim> Claims, DateTime ExpiresAt)> _claims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string UserId, DateTime ExpiresAt)> _refresh = new(StringComparer.Ordinal);
    private int _opsSinceSweep;

    public Task SetClaimsAsync(string accessToken, IReadOnlyList<Claim> claims, TimeSpan lifetime, CancellationToken ct = default)
    {
        _claims[accessToken] = (claims.Select(JournalClaim.From).ToList(), DateTime.Now + lifetime);
        SweepIfDue();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Claim>?> GetClaimsAsync(string accessToken, CancellationToken ct = default)
    {
        SweepIfDue();
        if (_claims.TryGetValue(accessToken, out var entry) && entry.ExpiresAt > DateTime.Now)
        {
            return Task.FromResult<IReadOnlyList<Claim>?>(entry.Claims.Select(c => c.ToClaim()).ToList());
        }
        _claims.TryRemove(accessToken, out _);
        return Task.FromResult<IReadOnlyList<Claim>?>(null);
    }

    public Task RemoveClaimsAsync(string accessToken, CancellationToken ct = default)
    {
        _claims.TryRemove(accessToken, out _);
        return Task.CompletedTask;
    }

    public Task AddRefreshAsync(string userId, string refreshToken, TimeSpan lifetime, CancellationToken ct = default)
    {
        _refresh[refreshToken] = (userId, DateTime.Now + lifetime);
        SweepIfDue();
        return Task.CompletedTask;
    }

    public Task<bool> HasRefreshAsync(string userId, string refreshToken, CancellationToken ct = default)
    {
        SweepIfDue();
        if (_refresh.TryGetValue(refreshToken, out var entry)
            && entry.UserId == userId && entry.ExpiresAt > DateTime.Now)
        {
            return Task.FromResult(true);
        }
        _refresh.TryRemove(refreshToken, out _);
        return Task.FromResult(false);
    }

    public Task RemoveRefreshAsync(string userId, string refreshToken, CancellationToken ct = default)
    {
        if (_refresh.TryGetValue(refreshToken, out var entry) && entry.UserId == userId)
        {
            _refresh.TryRemove(refreshToken, out _);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAllUserAsync(string userId, CancellationToken ct = default)
    {
        foreach (var (token, entry) in _refresh)
        {
            if (entry.UserId == userId)
            {
                _refresh.TryRemove(token, out _);
            }
        }
        return Task.CompletedTask;
    }

    private void SweepIfDue()
    {
        if (Interlocked.Increment(ref _opsSinceSweep) < 256)
        {
            return;
        }
        Interlocked.Exchange(ref _opsSinceSweep, 0);
        var now = DateTime.Now;
        foreach (var (token, entry) in _claims)
        {
            if (entry.ExpiresAt <= now)
            {
                _claims.TryRemove(token, out _);
            }
        }
        foreach (var (token, entry) in _refresh)
        {
            if (entry.ExpiresAt <= now)
            {
                _refresh.TryRemove(token, out _);
            }
        }
    }
}
