namespace NexMesh.Core.Context;

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// 请求入口快照:把 HttpContext 的身份/租户/链路投影为 NexMeshContext 写入环境槽,
/// 使请求内后台分支(Task.Run/工具调用)与离线捕获(Capture)有统一且一致的上下文源。
/// </summary>
public class NexMeshContextMiddleware
{
    private readonly RequestDelegate _next;

    public NexMeshContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, INexMeshContextAccessor accessor)
    {
        var claims = context.User.Claims
            .Where(c => !string.IsNullOrEmpty(c.Value))
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value!);

        var snapshot = new NexMeshContext
        {
            UserId = claims.GetValueOrDefault(ClaimTypes.NameIdentifier) ?? claims.GetValueOrDefault("sub"),
            UserName = context.User.Identity?.Name,
            TenantId = claims.GetValueOrDefault("tenantid")
                ?? claims.GetValueOrDefault("hospitalcode")
                ?? claims.GetValueOrDefault("tenantcode"),
            Claims = claims,
            TraceContext = Activity.Current?.Context ?? default,
        };

        using (accessor.Enter(snapshot))
        using (TenantHelper.Enter(snapshot.TenantId)) // SSS+第二轮:作用域式绑定(还原先前值,防槽泄漏)
        {
            await _next(context);
        }
    }
}

public static class NexMeshContextAppExtensions
{
    /// <summary>启用请求上下文快照(建议置于管道最前)。</summary>
    public static IApplicationBuilder UseNexMeshContext(this IApplicationBuilder app)
        => app.UseMiddleware<NexMeshContextMiddleware>();
}
