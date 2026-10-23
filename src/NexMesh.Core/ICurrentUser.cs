namespace NexMesh.Core;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexMesh.Core.Context;

/// <summary>
/// 当前用户上下文:双源统一——环境槽(AsyncLocal 显式绑定,请求内后台分支与离线任务可用)
/// 优先,HttpContext 兜底。未认证时各属性为 null/false。
/// </summary>
public interface ICurrentUser
{
    string? UserId { get; }
    string? UserName { get; }

    /// <summary>租户/院区标识(tenantid/hospitalcode/tenantcode 声明,离线任务同样可读)。</summary>
    string? TenantId { get; }

    bool IsAuthenticated { get; }
    Claim? FindClaim(string claimType);
}

/// <summary>双源默认实现:环境槽(显式绑定)优先,HttpContext 兜底。</summary>
public sealed class CurrentUser(INexMeshContextAccessor ambient, IHttpContextAccessor http) : ICurrentUser
{
    private NexMeshContext? Ambient => ambient.Current;
    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public string? UserId =>
        Ambient?.UserId
        ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Principal?.FindFirst("sub")?.Value;

    public string? UserName => Ambient?.UserName ?? Principal?.Identity?.Name;

    public string? TenantId =>
        Ambient?.TenantId
        ?? Principal?.FindFirst("tenantid")?.Value
        ?? Principal?.FindFirst("hospitalcode")?.Value
        ?? Principal?.FindFirst("tenantcode")?.Value;

    public bool IsAuthenticated =>
        (Ambient is not null && !string.IsNullOrEmpty(Ambient.UserId))
        || Principal?.Identity?.IsAuthenticated == true;

    public Claim? FindClaim(string claimType) =>
        Ambient?.Claims.TryGetValue(claimType, out var value) == true
            ? new Claim(claimType, value)
            : Principal?.FindFirst(claimType);
}
