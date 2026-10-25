namespace NexMesh.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 登录限流(暴力破解面):per-IP 固定窗口,窗口内超限返回 429。
/// 计数经 ILoginRateLimitCounter 抽象:内存(单实例)或 Redis(Lua 原子窗口,多实例全局一致);
/// 配置 NexMesh:Auth:LoginRateLimit:PermitLimit(默认 5)/ WindowSeconds(默认 60)/ Enabled(默认 true)
/// / Store(Auto|Memory|Redis,默认 Auto——容器有 IConnectionMultiplexer 即 Redis)/ RedisFailMode(Open|Closed,默认 Open)。
/// </summary>
public sealed class LoginRateLimiter
{
    private readonly int _permitLimit;
    private readonly int _windowSeconds;
    private readonly bool _enabled;
    private readonly ILoginRateLimitCounter _counter;

    public LoginRateLimiter(IConfiguration configuration, TimeProvider? timeProvider = null)
        : this(configuration, new MemoryLoginRateLimitCounter(timeProvider)) { }

    public LoginRateLimiter(IConfiguration configuration, ILoginRateLimitCounter counter)
    {
        var section = configuration.GetSection("NexMesh:Auth:LoginRateLimit");
        _enabled = section.GetValue<bool?>("Enabled") ?? true;
        _permitLimit = section.GetValue<int?>("PermitLimit") ?? 5;
        _windowSeconds = section.GetValue<int?>("WindowSeconds") ?? 60;
        _counter = counter;
    }

    /// <summary>检查并计数;超限返回 false(调用方回 429)。计数器故障由其 FailOpen 策略决定。</summary>
    public async Task<bool> TryPassAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return true;
        }
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return await _counter.TryPassAsync(key, _permitLimit, _windowSeconds, cancellationToken);
    }
}

/// <summary>登录限流注册(独立入口;AddNexMeshAuth 已默认装配,通常无需手动调用)。</summary>
public static class LoginRateLimiterExtensions
{
    public static IServiceCollection AddNexMeshLoginRateLimiter(this IServiceCollection services)
    {
        services.AddSingleton<LoginRateLimiter>();
        return services;
    }
}
