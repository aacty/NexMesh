using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexMesh;
using NexMesh.Core.DependencyInjection;

namespace NexMesh.AiDemo.Services;

/// <summary>端点限流演示:10 秒内同 IP 只能调 3 次,第 4 次起 429(统一信封)。</summary>
[ApiContract("ratelimit")]
public interface IRateLimitApi : IServiceApi
{
    /// <summary>限流演示端点(PermitLimit=3/10s)。</summary>
    [AllowAnonymous]
    [HttpGet("ping")]
    [EnableRateLimiting("demo-burst")]
    Task<string> PingAsync();
}

public class RateLimitService : IRateLimitApi, IScopedService
{
    public Task<string> PingAsync() => Task.FromResult($"pong @ {DateTime.Now:HH:mm:ss}");
}
