namespace NexMesh.Auth;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexMesh.Core.Models;
using System.Text.Encodings.Web;

/// <summary>
/// 不透明 Token 认证 Handler(标准管线,注册为默认 Scheme "NexMeshToken"):
/// Bearer 解析 → 会话查找(无效/过期/吊销 → NoResult)→ 滑动续期 → ClaimsPrincipal。
/// 之后快照中间件/ICurrentUser 双源自然读到身份与租户;Challenge 统一 401 业务码。
/// </summary>
public sealed class NexMeshTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    NexMeshTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult(); // 匿名:由端点授权策略决定
        }

        var token = authorization["Bearer ".Length..].Trim();
        var session = await tokens.GetAsync(token);
        if (session is null)
        {
            return AuthenticateResult.Fail("invalid-token");
        }

        var (renewed, _) = await tokens.TouchAsync(session);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, renewed.UserId),
            new("sub", renewed.UserId),
        };
        if (renewed.UserName is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, renewed.UserName));
        }
        if (renewed.TenantId is not null)
        {
            claims.Add(new Claim("tenantid", renewed.TenantId));
        }
        foreach (var (key, value) in renewed.Claims)
        {
            claims.Add(new Claim(key, value));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: Scheme.Name));
        Context.Items["NexMesh.Token"] = renewed.Token; // 登出端点取用
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>统一 401:带了 Bearer 但无效/过期/吊销 → 40101 业务码;匿名被拒 → 40101 提示需登录。</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var message = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? "访问令牌无效、已过期或已吊销"
            : "该操作需要登录(缺少 Bearer 访问令牌)";

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(ApiResult<object>.Fail(40101, message)));
    }
}

public static class NexMeshAuthAppExtensions
{
    /// <summary>
    /// 启用 NexMesh Token 认证 = UseAuthentication + 无效凭据拒绝闸。
    /// 闸语义:携带了 Bearer 令牌但未通过认证(无效/过期/吊销)→ 一律 401 业务码,
    /// 绝不静默降级为匿名(安全惯例:可疑凭据必须显式拒绝)。
    /// 须在 UseNexMeshContext/UseNexMeshService 之前注册;UseAuthorization 须在其后(建议显式)。
    /// </summary>
    public static IApplicationBuilder UseNexMeshAuthentication(this IApplicationBuilder app)
        => app.UseAuthentication().Use(RejectInvalidBearerMiddleware);

    private static async Task RejectInvalidBearerMiddleware(HttpContext context, Func<Task> next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResult<object>.Fail(40101, "访问令牌无效、已过期或已吊销")));
            return;
        }
        await next();
    }
}
