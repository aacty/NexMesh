namespace NexMesh.Auth;

using System.Collections.Concurrent;

/// <summary>
/// ITokenStore 内存实现:访问令牌校验直查会话存储(真实会话有效性);
/// 刷新令牌经声明日志白名单;用户→令牌索引支持全端登出。
/// 多实例集群部署用 RedisTokenStore 替代。
/// </summary>
public sealed class MemoryTokenStore(ISessionStore sessions, IClaimsJournal journal) : ITokenStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userAccessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userRefreshTokens = new(StringComparer.Ordinal);

    public async Task StoreAccessTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default)
    {
        _userAccessTokens.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(token, 0);
        await Task.CompletedTask;
    }

    public async Task StoreRefreshTokenAsync(string userId, string token, TimeSpan lifetime, CancellationToken ct = default)
    {
        _userRefreshTokens.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(token, 0);
        await journal.AddRefreshAsync(userId, token, lifetime, ct);
    }

    public async Task<bool> ValidateAccessTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync(token);
        return session is { Revoked: false } && session.UserId == userId;
    }

    public Task<bool> ValidateRefreshTokenAsync(string userId, string token, CancellationToken ct = default) =>
        journal.HasRefreshAsync(userId, token, ct);

    public async Task RemoveAccessTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        // 吊销真实会话:令牌立即失效(与认证管线联动)
        await sessions.RevokeAsync(token);
        if (_userAccessTokens.TryGetValue(userId, out var tokens))
        {
            tokens.TryRemove(token, out _);
        }
        await journal.RemoveClaimsAsync(token, ct);
    }

    public async Task RemoveRefreshTokenAsync(string userId, string token, CancellationToken ct = default)
    {
        if (_userRefreshTokens.TryGetValue(userId, out var tokens))
        {
            tokens.TryRemove(token, out _);
        }
        await journal.RemoveRefreshAsync(userId, token, ct);
    }

    public async Task RemoveAllTokensAsync(string userId, CancellationToken ct = default)
    {
        // 全端登出:吊销该用户全部访问令牌会话
        if (_userAccessTokens.TryGetValue(userId, out var accessTokens))
        {
            foreach (var token in accessTokens.Keys)
            {
                await sessions.RevokeAsync(token);
            }
        }
        _userAccessTokens.TryRemove(userId, out _);
        _userRefreshTokens.TryRemove(userId, out _);
        await journal.RemoveAllUserAsync(userId, ct);
    }
}
