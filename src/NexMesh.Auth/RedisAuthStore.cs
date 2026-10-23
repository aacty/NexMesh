namespace NexMesh.Auth;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// Redis 认证存储:一套实现同时承担会话(ISessionStore)、声明日志+刷新白名单(IClaimsJournal)
/// 与令牌白名单(ITokenStore)——多实例集群共享登录态,重启不失效(优于 JWT 白名单内存形态)。
/// Key 布局(前缀可配,默认 nexmesh:auth:):
///   session:{token} / claims:{token} / refresh:{token} / user-tokens:{userId}(Set) / user-refresh:{userId}(Set)
/// </summary>
public sealed class RedisAuthStore(
    IConnectionMultiplexer redis,
    string keyPrefix = "nexmesh:auth:") : ISessionStore, IClaimsJournal, ITokenStore
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private IDatabase Db => redis.GetDatabase();
    private string SessionKey(string token) => $"{keyPrefix}session:{token}";
    private string ClaimsKey(string token) => $"{keyPrefix}claims:{token}";
    private string RefreshKey(string token) => $"{keyPrefix}refresh:{token}";
    private RedisKey UserTokensKey(string userId) => new($"{keyPrefix}user-tokens:{userId}");
    private RedisKey UserRefreshKey(string userId) => new($"{keyPrefix}user-refresh:{userId}");

    // ── ISessionStore ──────────────────────────────────────────────

    public async Task SaveAsync(TokenSession session)
    {
        var ttl = session.ExpiresAt - DateTime.Now;
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }
        await Db.StringSetAsync(SessionKey(session.Token), JsonSerializer.Serialize(session, Json), ttl);
    }

    public async Task<TokenSession?> GetAsync(string token)
    {
        var value = await Db.StringGetAsync(SessionKey(token));
        if (value.IsNullOrEmpty)
        {
            return null;
        }
        try
        {
            var session = JsonSerializer.Deserialize<TokenSession>(value.ToString(), Json);
            if (session is null || session.Revoked || session.ExpiresAt <= DateTime.Now)
            {
                return null;
            }
            return session;
        }
        catch (JsonException)
        {
            return null; // 异常数据视为无会话(fail-closed)
        }
    }

    public async Task RevokeAsync(string token)
    {
        await Db.KeyDeleteAsync(SessionKey(token));
        await Db.KeyDeleteAsync(ClaimsKey(token));
    }

    // ── IClaimsJournal ─────────────────────────────────────────────

    public async Task SetClaimsAsync(string accessToken, IReadOnlyList<Claim> claims, TimeSpan lifetime, CancellationToken ct = default)
    {
        var entries = claims.Select(JournalClaim.From).ToList();
        await Db.StringSetAsync(ClaimsKey(accessToken), JsonSerializer.Serialize(entries, Json), lifetime);
    }

    public async Task<IReadOnlyList<Claim>?> GetClaimsAsync(string accessToken, CancellationToken ct = default)
    {
        var value = await Db.StringGetAsync(ClaimsKey(accessToken));
        if (value.IsNullOrEmpty)
        {
            return null;
        }
        try
        {
            var entries = JsonSerializer.Deserialize<List<JournalClaim>>(value.ToString(), Json);
            return entries?.Select(e => e.ToClaim()).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task RemoveClaimsAsync(string accessToken, CancellationToken ct = default) =>
        Db.KeyDeleteAsync(ClaimsKey(accessToken));

    public Task AddRefreshAsync(string userId, string refreshToken, TimeSpan lifetime, CancellationToken ct = default)
    {
        var script = @"
            redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
            redis.call('SADD', KEYS[2], KEYS[1])
            return 1";
        var db = redis.GetDatabase();
        return db.ScriptEvaluateAsync(script,
            new RedisKey[] { RefreshKey(refreshToken), UserRefreshKey(userId) },
            new RedisValue[] { userId, (long)lifetime.TotalSeconds });
    }

    public async Task<bool> HasRefreshAsync(string userId, string refreshToken, CancellationToken ct = default)
    {
        var stored = await Db.StringGetAsync(RefreshKey(refreshToken));
        return !stored.IsNullOrEmpty && stored == userId;
    }

    public Task RemoveRefreshAsync(string userId, string refreshToken, CancellationToken ct = default)
    {
        var script = @"
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                redis.call('DEL', KEYS[1])
                redis.call('SREM', KEYS[2], KEYS[1])
            end
            return 1";
        var db = redis.GetDatabase();
        return db.ScriptEvaluateAsync(script,
            new RedisKey[] { RefreshKey(refreshToken), UserRefreshKey(userId) },
            new RedisValue[] { userId });
    }

    public async Task RemoveAllUserAsync(string userId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var refreshTokens = await db.SetMembersAsync(UserRefreshKey(userId));
        var keys = new List<RedisKey> { UserRefreshKey(userId) };
        keys.AddRange(refreshTokens.Select(t => (RedisKey)RefreshKey(t.ToString())));
        await db.KeyDeleteAsync(keys.ToArray());
    }

    // ── ITokenStore ────────────────────────────────────────────────

    public async Task StoreAccessTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default)
    {
        // 会话已由签发流程落库;此处仅维护用户索引(RemoveAllTokens 全端登出用)
        await Db.SetAddAsync(UserTokensKey(userId), token);
        await Db.KeyExpireAsync(UserTokensKey(userId), lifetime);
    }

    public async Task StoreRefreshTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default)
    {
        await AddRefreshAsync(userId, token, lifetime, ct);
    }

    public async Task<bool> ValidateAccessTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        var session = await GetAsync(token);
        return session is { Revoked: false } && session.UserId == userId;
    }

    public Task<bool> ValidateRefreshTokenAsync(string userId, string token, CancellationToken ct = default) =>
        HasRefreshAsync(userId, token, ct);

    public async Task RemoveAccessTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        // 吊销真实会话 + 清理索引与声明日志
        await RevokeAsync(token);
        await Db.SetRemoveAsync(UserTokensKey(userId), token);
    }

    public Task RemoveRefreshTokenAsync(string userId, string token, CancellationToken ct = default) =>
        RemoveRefreshAsync(userId, token, ct);

    public async Task RemoveAllTokensAsync(string userId, CancellationToken ct = default)
    {
        // 全端登出:吊销全部访问令牌会话
        var tokens = await Db.SetMembersAsync(UserTokensKey(userId));
        foreach (var token in tokens)
        {
            await RevokeAsync(token.ToString());
        }
        await RemoveAllUserAsync(userId, ct);
        await Db.KeyDeleteAsync(UserTokensKey(userId));
    }
}

public static class RedisAuthStoreExtensions
{
    /// <summary>
    /// 注册 Redis 认证存储:覆盖内存默认的会话存储/声明日志/令牌白名单(后注册者生效)。
    /// 多实例集群部署必须调用;单实例可继续用内存实现。
    /// </summary>
    public static IServiceCollection AddNexMeshRedisAuthStore(
        this IServiceCollection services, string connectionString, string keyPrefix = "nexmesh:auth:")
    {
        var multiplexer = ConnectionMultiplexer.Connect(connectionString);
        var store = new RedisAuthStore(multiplexer, keyPrefix);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddSingleton<ISessionStore>(store);
        services.AddSingleton<IClaimsJournal>(store);
        services.AddSingleton<ITokenStore>(store);
        return services;
    }
}
