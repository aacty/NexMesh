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
    string? Email { get; }

    /// <summary>角色集合(ClaimTypes.Role 全部声明)。</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>租户/院区标识(tenantid/hospitalcode/tenantcode 声明,离线任务同样可读)。</summary>
    string? TenantId { get; }

    bool IsAuthenticated { get; }

    /// <summary>认证方案名(未认证为 null)。</summary>
    string? AuthenticationType { get; }

    /// <summary>当前请求的 ClaimsPrincipal(无 HttpContext 时退环境槽构建)。</summary>
    ClaimsPrincipal? Principal { get; }

    bool IsInRole(string role);

    Claim? FindClaim(string claimType);

    /// <summary>取指定声明类型的第一个值。</summary>
    string? FindClaimValue(string claimType);

    /// <summary>取指定声明类型的全部值。</summary>
    IReadOnlyList<string> FindClaimValues(string claimType);
}

/// <summary>
/// 强类型当前用户:按约定把 Claims 反序列化为业务用户模型——
/// Id/UserId→NameIdentifier、Name/UserName→Name、Email→Email、Roles→Role 声明(展开),
/// 其余属性→属性名小写声明;集合属性以 JSON 数组承载。[ClaimType("...")] 可覆盖属性名。
/// </summary>
public interface ICurrentUser<out TUser> : ICurrentUser where TUser : class
{
    /// <summary>业务用户模型(未认证为 null)。</summary>
    TUser? User { get; }
}

/// <summary>双源默认实现:环境槽(显式绑定)优先,HttpContext 兜底。</summary>
public sealed class CurrentUser(INexMeshContextAccessor ambient, IHttpContextAccessor http) : ICurrentUser
{
    private NexMeshContext? Ambient => ambient.Current;
    private ClaimsPrincipal? HttpPrincipal => http.HttpContext?.User;

    public string? UserId =>
        Ambient?.UserId
        ?? HttpPrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? HttpPrincipal?.FindFirst("sub")?.Value;

    public string? UserName => Ambient?.UserName ?? HttpPrincipal?.Identity?.Name;

    public string? Email =>
        Ambient?.Claims.TryGetValue(ClaimTypes.Email, out var ambientEmail) == true
            ? ambientEmail
            : HttpPrincipal?.FindFirst(ClaimTypes.Email)?.Value;

    public IReadOnlyList<string> Roles =>
        FindClaimValues(ClaimTypes.Role);

    public string? TenantId =>
        Ambient?.TenantId
        ?? HttpPrincipal?.FindFirst("tenantid")?.Value
        ?? HttpPrincipal?.FindFirst("hospitalcode")?.Value
        ?? HttpPrincipal?.FindFirst("tenantcode")?.Value;

    public bool IsAuthenticated =>
        (Ambient is not null && !string.IsNullOrEmpty(Ambient.UserId))
        || HttpPrincipal?.Identity?.IsAuthenticated == true;

    public string? AuthenticationType =>
        HttpPrincipal?.Identity?.IsAuthenticated == true ? HttpPrincipal.Identity.AuthenticationType : null;

    public ClaimsPrincipal? Principal => HttpPrincipal;

    public bool IsInRole(string role) =>
        HttpPrincipal?.IsInRole(role) == true;

    public Claim? FindClaim(string claimType) =>
        Ambient?.Claims.TryGetValue(claimType, out var value) == true
            ? new Claim(claimType, value)
            : HttpPrincipal?.FindFirst(claimType);

    public string? FindClaimValue(string claimType) =>
        Ambient?.Claims.TryGetValue(claimType, out var ambientValue) == true
            ? ambientValue
            : HttpPrincipal?.FindFirst(claimType)?.Value;

    public IReadOnlyList<string> FindClaimValues(string claimType) =>
        Ambient?.Claims.TryGetValue(claimType, out var ambientValue) == true
            ? [ambientValue]
            : HttpPrincipal?.FindAll(claimType).Select(c => c.Value).ToList() ?? [];
}

/// <summary>强类型当前用户实现:TUser 从 Claims 按约定重建。</summary>
public sealed class CurrentUser<TUser>(ICurrentUser inner) : ICurrentUser<TUser> where TUser : class, new()
{
    public TUser? User =>
        inner.IsAuthenticated && inner.Principal is { } principal
            ? UserClaimsMapper.FromClaims<TUser>(principal.Claims)
            : null;

    public string? UserId => inner.UserId;
    public string? UserName => inner.UserName;
    public string? Email => inner.Email;
    public IReadOnlyList<string> Roles => inner.Roles;
    public string? TenantId => inner.TenantId;
    public bool IsAuthenticated => inner.IsAuthenticated;
    public string? AuthenticationType => inner.AuthenticationType;
    public ClaimsPrincipal? Principal => inner.Principal;
    public bool IsInRole(string role) => inner.IsInRole(role);
    public Claim? FindClaim(string claimType) => inner.FindClaim(claimType);
    public string? FindClaimValue(string claimType) => inner.FindClaimValue(claimType);
    public IReadOnlyList<string> FindClaimValues(string claimType) => inner.FindClaimValues(claimType);
}

/// <summary>声明类型覆盖:默认取属性名小写,[ClaimType("...")] 显式指定。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ClaimTypeAttribute(string claimType) : Attribute
{
    public string ClaimType { get; } = claimType;
}
