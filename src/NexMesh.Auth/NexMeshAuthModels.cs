namespace NexMesh.Auth;

using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>NexMesh:Auth 配置节。</summary>
public sealed class NexMeshAuthOptions
{
    /// <summary>访问令牌有效期(分钟),SPD 口径默认 720(12 小时)。</summary>
    public int AccessTokenExpireMinutes { get; set; } = 720;

    /// <summary>滑动续期:最后活动进入"临期窗口"时自动延长。关闭则固定过期。</summary>
    public bool SlidingEnabled { get; set; } = true;

    /// <summary>临期窗口(分钟):剩余寿命低于该值时续期。</summary>
    public int NearlyExpiredMinutes { get; set; } = 10;

    /// <summary>会话最大总寿命(分钟),滑动续期不可突破的硬上限。0 = 不设上限。</summary>
    public int MaxLifetimeMinutes { get; set; } = 7 * 24 * 60;
}

/// <summary>登录成功返回的用户身份(业务验证器产出)。</summary>
public sealed record LoginUser(
    string UserId,
    string? UserName = null,
    string? TenantId = null,
    IReadOnlyDictionary<string, string>? Claims = null)
{
    /// <summary>多值声明(如多个 Role):Claims 字典无法承载同类型多条,列表补充;null 时行为不变。</summary>
    public IReadOnlyList<TokenClaim>? ClaimList { get; init; }
}

/// <summary>序列化安全的声明条目(Type/Value 键值对)。</summary>
public sealed record TokenClaim(string Type, string Value);

/// <summary>
/// 业务登录验证扩展点:业务项目实现并注册(用户名密码校验、扫码、短信等均由此接入)。
/// </summary>
public interface ILoginValidator
{
    /// <summary>验证失败返回 null(框架统一回 401 业务码 40102)。</summary>
    Task<LoginUser?> ValidateAsync(string username, string password);
}

/// <summary>Token 会话(服务端状态——不透明 Token 的安全性正在于可吊销可续期)。</summary>
public sealed record TokenSession
{
    public string Token { get; init; } = "";
    public string UserId { get; init; } = "";
    public string? UserName { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    /// <summary>多值声明(如多个 Role);存在时认证处理器以其为权威来源。null 时行为不变。</summary>
    public IReadOnlyList<TokenClaim>? ClaimList { get; init; }

    public DateTime IssuedAt { get; init; } = DateTime.Now;
    public DateTime ExpiresAt { get; init; }
    public DateTime LastActivityAt { get; init; } = DateTime.Now;
    public bool Revoked { get; init; }
}

/// <summary>会话存储:内存默认(Redis 实现随缓存包交付,接口不变)。</summary>
public interface ISessionStore
{
    Task SaveAsync(TokenSession session);
    Task<TokenSession?> GetAsync(string token);
    Task RevokeAsync(string token);

    /// <summary>
    /// 列出活跃(未吊销未过期)会话摘要(Dashboard/运维用;默认空——自定义实现者不受影响,
    /// 内存/Redis 内置实现提供)。摘要不含 Token 明文(仅前缀),防泄露。
    /// </summary>
    Task<List<SessionSummary>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<SessionSummary>());

    /// <summary>吊销某用户全部活跃会话(Dashboard 踢下线;默认空——自定义实现者不受影响)。返回吊销条数。</summary>
    Task<int> RevokeByUserAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>会话摘要(Dashboard 展示;Token 打码)。</summary>
public sealed record SessionSummary(
    string TokenPrefix,
    string UserId,
    string? UserName,
    string? TenantId,
    DateTime IssuedAt,
    DateTime LastActivityAt,
    DateTime ExpiresAt);

/// <summary>内存会话存储(单实例部署/开发/测试;多实例集群用 Redis 实现)。</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, TokenSession> _sessions = new(StringComparer.Ordinal);
    private int _opsSinceSweep;

    public Task SaveAsync(TokenSession session)
    {
        _sessions[session.Token] = session;
        SweepIfDue();
        return Task.CompletedTask;
    }

    public Task<TokenSession?> GetAsync(string token)
    {
        SweepIfDue();
        return Task.FromResult(_sessions.TryGetValue(token, out var session)
            && !session.Revoked
            && session.ExpiresAt > DateTime.Now
                ? session
                : null);
    }

    public Task RevokeAsync(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
        {
            _sessions[session.Token] = session with { Revoked = true };
        }
        return Task.CompletedTask;
    }

    /// <summary>活跃会话清单(快照语义:枚举期间新建的会话可能缺席,展示用途可接受)。</summary>
    public Task<List<SessionSummary>> ListActiveAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var list = _sessions.Values
            .Where(s => !s.Revoked && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActivityAt)
            .Select(s => new SessionSummary(
                s.Token.Length > 8 ? s.Token[..8] + "…" : s.Token,
                s.UserId, s.UserName, s.TenantId, s.IssuedAt, s.LastActivityAt, s.ExpiresAt))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> RevokeByUserAsync(string userId, CancellationToken ct = default)
    {
        var targets = _sessions.Values
            .Where(s => s.UserId == userId && !s.Revoked)
            .ToList();
        foreach (var session in targets)
        {
            _sessions[session.Token] = session with { Revoked = true };
        }
        return Task.FromResult(targets.Count);
    }

    /// <summary>
    /// B8:惰性清扫——每 256 次操作或距上次清扫 5 分钟,清除已过期/已吊销会话,
    /// 防止单例字典随登录次数无界增长(内存 DoS 面)。
    /// </summary>
    private void SweepIfDue()
    {
        if (Interlocked.Increment(ref _opsSinceSweep) < 256)
        {
            return;
        }
        Interlocked.Exchange(ref _opsSinceSweep, 0);
        var now = DateTime.Now;
        foreach (var kv in _sessions)
        {
            if (kv.Value.Revoked || kv.Value.ExpiresAt <= now)
            {
                _sessions.TryRemove(kv.Key, out _);
            }
        }
    }
}

/// <summary>不透明 Token 签发与滑动续期。</summary>
public sealed class NexMeshTokenService(ISessionStore store, Microsoft.Extensions.Options.IOptions<NexMeshAuthOptions> options)
{
    private readonly NexMeshAuthOptions _options = options.Value;
    public async Task<TokenSession> IssueAsync(LoginUser user)
    {
        var now = DateTime.Now;
        var session = new TokenSession
        {
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            UserId = user.UserId,
            UserName = user.UserName,
            TenantId = user.TenantId,
            Claims = user.Claims ?? new Dictionary<string, string>(),
            ClaimList = user.ClaimList,
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(_options.AccessTokenExpireMinutes),
        };
        await store.SaveAsync(session);
        return session;
    }

    /// <summary>滑动续期:临期窗口内活动延长寿命(不突破最大总寿命)。返回(会话, 是否续期)。</summary>
    public async Task<(TokenSession Session, bool Renewed)> TouchAsync(TokenSession session)
    {
        if (!_options.SlidingEnabled)
        {
            return (session, false);
        }
        var now = DateTime.Now;
        if (session.ExpiresAt - now > TimeSpan.FromMinutes(_options.NearlyExpiredMinutes))
        {
            return (session with { LastActivityAt = now }, false);
        }
        var maxLifetime = _options.MaxLifetimeMinutes > 0
            ? session.IssuedAt.AddMinutes(_options.MaxLifetimeMinutes)
            : DateTime.MaxValue;
        var renewed = session with
        {
            ExpiresAt = now.AddMinutes(_options.AccessTokenExpireMinutes) < maxLifetime
                ? now.AddMinutes(_options.AccessTokenExpireMinutes)
                : maxLifetime,
            LastActivityAt = now,
        };
        await store.SaveAsync(renewed);
        return (renewed, true);
    }

    public Task RevokeAsync(string token) => store.RevokeAsync(token);

    public Task<TokenSession?> GetAsync(string token) => store.GetAsync(token);
}
