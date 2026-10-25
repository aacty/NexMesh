namespace NexMesh;

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// 端点限流标记:AddNexMeshRateLimiter 注册后存在,宿主管线据此挂 UseRateLimiter;
/// 未注册时不挂中间件(零开销零影响)。RegisteredPolicies 供启动期校验端点引用的策略名。
/// </summary>
public sealed class NexMeshRateLimitMarker
{
    /// <summary>经配置注册的策略名(configure 回调注册的自定义策略不在其内,校验时单独提示)。</summary>
    public HashSet<string> RegisteredPolicies { get; } = new(StringComparer.Ordinal);

    /// <summary>是否提供了代码级自定义注册(有则未知策略名的告警措辞降级)。</summary>
    public bool HasCustomConfiguration { get; internal set; }
}

/// <summary>策略配置项(NexMesh:RateLimit:Policies:{name})。</summary>
public sealed class NexMeshRateLimitPolicyOptions
{
    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;

    /// <summary>分区维度:Ip(按客户端 IP)/ User(按登录用户,匿名回退 IP)。</summary>
    public string Partition { get; set; } = "Ip";
}

/// <summary>NexMesh 端点限流注册入口。</summary>
public static class NexMeshRateLimitServiceCollectionExtensions
{
    /// <summary>
    /// 注册端点限流:包装 ASP.NET Core RateLimiting——统一 429 ApiResult 信封(业务码 429);
    /// 按 NexMesh:RateLimit:Policies:{name} {PermitLimit, WindowSeconds, Partition=Ip|User} 注册命名固定窗口策略;
    /// configure 可追加代码级自定义策略(分区键/算法自选)。契约方法标 [EnableRateLimiting("策略名")] 即生效。
    /// </summary>
    public static IServiceCollection AddNexMeshRateLimiter(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RateLimiterOptions>? configure = null)
    {
        var policies = new Dictionary<string, NexMeshRateLimitPolicyOptions>(StringComparer.Ordinal);
        configuration.GetSection("NexMesh:RateLimit:Policies").Bind(policies);

        var marker = new NexMeshRateLimitMarker();
        foreach (var name in policies.Keys)
        {
            marker.RegisteredPolicies.Add(name);
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = static async (context, cancellationToken) =>
            {
                // 与全框架信封同源:真实 ApiResult 序列化(不手搓 JSON)
                context.HttpContext.Response.ContentType = "application/json";
                var envelope = ApiResult<object?>.Fail("请求过于频繁,请稍后再试", 429);
                envelope.TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
                await context.HttpContext.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(envelope, System.Text.Json.JsonSerializerOptions.Web),
                    cancellationToken);
            };

            foreach (var (name, policy) in policies)
            {
                var byUser = policy.Partition.Equals("User", StringComparison.OrdinalIgnoreCase);
                var permitLimit = policy.PermitLimit;
                var window = TimeSpan.FromSeconds(policy.WindowSeconds);
                options.AddPolicy<string>(name, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        PartitionKey(context, byUser),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = window,
                            QueueLimit = 0,
                        }));
            }

            configure?.Invoke(options);
        });
        if (configure is not null)
        {
            marker.HasCustomConfiguration = true;
        }
        services.AddSingleton(marker);
        return services;
    }

    /// <summary>分区键:User 维度取登录用户标识(匿名回退 IP),Ip 维度取客户端 IP。</summary>
    private static string PartitionKey(HttpContext context, bool byUser)
    {
        if (byUser)
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                return $"user:{userId}";
            }
        }
        return $"ip:{context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None}";
    }
}
