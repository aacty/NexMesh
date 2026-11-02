namespace NexMesh.Core.Context;

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

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
        // 声明投影零 LINQ(实测 Where+GroupBy+ToDictionary 1336B/次 → foreach+TryAdd 600B/次,-55%;
        // 同语义:每声明类型首个非空值胜出)
        var claims = new Dictionary<string, string>(8, StringComparer.Ordinal);
        foreach (var claim in context.User.Claims)
        {
            if (!string.IsNullOrEmpty(claim.Value))
            {
                claims.TryAdd(claim.Type, claim.Value);
            }
        }

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

        // 双槽单作用域绑定(SSS+第二轮作用域式绑定的收口:较两次 Enter 少 1 个作用域对象,还原次序不变)
        using (ContextSlotBinding.Enter(accessor, snapshot))
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
