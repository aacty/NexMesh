namespace NexMesh.Auth;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 登录限流(B5,暴力破解面):固定窗口 per-IP 计数,窗口内超限返回 429 业务码 42901。
/// 内嵌于登录端点处理器(不依赖 UseRateLimiter 中间件顺序),零额外管线配置。
/// 配置 NexMesh:Auth:LoginRateLimit:PermitLimit(默认 5)/ WindowSeconds(默认 60)/ Enabled(默认 true)。
/// </summary>
public sealed class LoginRateLimiter
{
    private sealed record Window(DateTime Started, int Count);

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _permitLimit;
    private readonly int _windowSeconds;
    private readonly bool _enabled;

    public LoginRateLimiter(IConfiguration configuration)
    {
        var section = configuration.GetSection("NexMesh:Auth:LoginRateLimit");
        _enabled = section.GetValue<bool?>("Enabled") ?? true;
        _permitLimit = section.GetValue<int?>("PermitLimit") ?? 5;
        _windowSeconds = section.GetValue<int?>("WindowSeconds") ?? 60;
    }

    /// <summary>检查并计数;超限返回 false(调用方回 429)。</summary>
    public bool TryPass(HttpContext context)
    {
        if (!_enabled)
        {
            return true;
        }
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.Now;

        var passed = _windows.AddOrUpdate(key,
            _ => new Window(now, 1),
            (_, existing) =>
                now - existing.Started > TimeSpan.FromSeconds(_windowSeconds)
                    ? new Window(now, 1) // 窗口过期,重新计数
                    : existing with { Count = existing.Count + 1 });

        return passed.Count <= _permitLimit;
    }
}

/// <summary>登录限流注册。</summary>
public static class LoginRateLimiterExtensions
{
    public static IServiceCollection AddNexMeshLoginRateLimiter(this IServiceCollection services)
    {
        services.AddSingleton<LoginRateLimiter>();
        return services;
    }


}
