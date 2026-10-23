namespace NexMesh.Auth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

/// <summary>NexMesh.Auth 注册与端点。</summary>
public static class NexMeshAuthExtensions
{
    /// <summary>
    /// 注册认证:配置绑定(NexMesh:Auth / NexMesh:Authentication)、内存会话存储默认
    /// (多实例用 AddNexMeshRedisAuthStore 覆盖)、Token 服务 + IJwtTokenService(不透明令牌)
    /// + ITokenStore(业务令牌白名单)。业务须实现并注册 ILoginValidator。
    /// </summary>
    public static IServiceCollection AddNexMeshAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NexMeshAuthOptions>()
            .Configure(o => configuration.GetSection("NexMesh:Auth").Bind(o));
        services.AddOptions<AuthenticationOptions>()
            .Configure(o => configuration.GetSection("NexMesh:Authentication").Bind(o));
        // 占位 Scheme:让 RequireAuthorization 的 Challenge/Forbid 走标准管线(实际认证在中间件)
        services.AddAuthentication("NexMeshToken")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NexMeshTokenAuthHandler>("NexMeshToken", null);
        services.AddAuthorization(); // [Authorize]/RequireAuthorization 的授权服务(内置,业务零心智)
        services.AddSingleton<ISessionStore, MemorySessionStore>();
        services.AddSingleton<IClaimsJournal, MemoryClaimsJournal>();
        services.AddSingleton<ITokenStore, MemoryTokenStore>();
        services.AddSingleton<NexMeshTokenService>();
        services.AddSingleton<IJwtTokenService, NexMeshJwtTokenService>();
        services.AddSingleton(new LoginRateLimiter(configuration)); // B5:登录限流(配置直接注入,不依赖容器 IConfiguration)

        // 兼容配置节(NexMesh:Authentication)存在时,Jwt.AccessTokenExpirationMinutes 成为签发有效期权威
        var authSection = configuration.GetSection("NexMesh:Authentication");
        if (authSection.Exists())
        {
            var authOptions = new AuthenticationOptions();
            authSection.Bind(authOptions);
            services.Configure<NexMeshAuthOptions>(o =>
                o.AccessTokenExpireMinutes = authOptions.Jwt.AccessTokenExpirationMinutes);
        }
        return services;
    }

    /// <summary>
    /// 认证端点:POST {prefix}/login(限流)、POST {prefix}/logout、GET {prefix}/me。
    /// 默认前缀 nexmesh/auth——框架保留路径(与 /health、/scalar 同类),
    /// 不占业务的 api/{服务名} 命名空间,避免与业务版认证契约(如 api/Auth/me)路由撞车
    /// (ASP.NET Core 路由不区分大小写,同路径两条字面量端点会 AmbiguousMatchException 全灭)。
    /// </summary>
    public static IEndpointRouteBuilder MapNexMeshAuth(this IEndpointRouteBuilder endpoints, string routePrefix = "nexmesh/auth")
    {
        var prefix = routePrefix.Trim('/');
        endpoints.MapPost($"/{prefix}/login", async (LoginRequest req, HttpContext http) =>
        {
            // B5:per-IP 固定窗口限流,超限 429(42901)
            var limiter = http.RequestServices.GetRequiredService<LoginRateLimiter>();
            if (!limiter.TryPass(http))
            {
                return Results.Json(
                    ApiResult<object>.Fail("登录尝试过于频繁,请稍后再试", 429), statusCode: 429);
            }

            var validator = http.RequestServices.GetService<ILoginValidator>();
            if (validator is null)
            {
                return Results.Json(
                    ApiResult<object>.Fail("未配置 ILoginValidator,业务侧需实现并注册登录验证", 500),
                    statusCode: 500);
            }

            var user = await validator.ValidateAsync(req.Username, req.Password);
            if (user is null)
            {
                return Results.Json(
                    ApiResult<object>.Fail("用户名或密码错误", 401), statusCode: 401);
            }

            var session = await http.RequestServices.GetRequiredService<NexMeshTokenService>().IssueAsync(user);
            return Results.Ok(ApiResult<LoginResponse>.Ok(new LoginResponse(
                session.Token, session.ExpiresAt, session.UserId, session.UserName, session.TenantId)));
        }).AllowAnonymous();

        endpoints.MapPost($"/{prefix}/logout", async (HttpContext http, NexMeshTokenService tokens) =>
        {
            if (http.Items["NexMesh.Token"] is string token)
            {
                await tokens.RevokeAsync(token);
            }
            return Results.Ok(ApiResult<object>.Ok("已登出"));
        }).RequireAuthorization();

        endpoints.MapGet($"/{prefix}/me", (ICurrentUser user) =>
            Results.Ok(ApiResult<object>.Ok(new
            {
                user.UserId,
                user.UserName,
                user.TenantId,
                user.IsAuthenticated,
            }))).RequireAuthorization();

        return endpoints;
    }
}

/// <summary>登录请求体。</summary>
public record LoginRequest(string Username, string Password);

/// <summary>登录响应。</summary>
public record LoginResponse(string AccessToken, DateTime ExpiresAt, string UserId, string? UserName, string? TenantId);
