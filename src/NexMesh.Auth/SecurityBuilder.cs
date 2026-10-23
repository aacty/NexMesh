namespace NexMesh.Auth;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

/// <summary>
/// 安全装配器(AddSecurity 回调内使用):注册认证服务、当前用户实现、令牌存储、
/// 附加认证方案与授权策略——业务 Program 只需
/// builder.AddNexMeshHost(x => x.AddSecurity(s => s.AddCurrentUser&lt;SwUserInfo&gt;()));
/// </summary>
public sealed class SecurityBuilder(NexMeshBuilder builder)
{
    public NexMeshBuilder Builder { get; } = builder;

    /// <summary>注册强类型当前用户(ICurrentUser&lt;TUser&gt; → CurrentUser&lt;TUser&gt;)。</summary>
    public SecurityBuilder AddCurrentUser<TUser>() where TUser : class, new()
    {
        Builder.Services.AddScoped<ICurrentUser<TUser>, CurrentUser<TUser>>();
        return this;
    }

    /// <summary>自定义当前用户解析器注册。</summary>
    public SecurityBuilder AddCurrentUser<TUser, TResolver>()
        where TUser : class, new()
        where TResolver : class, ICurrentUser<TUser>
    {
        Builder.Services.AddScoped<ICurrentUser<TUser>, TResolver>();
        return this;
    }

    /// <summary>注册令牌存储(同时承担会话存储/声明日志/令牌白名单,覆盖内存默认)。</summary>
    public SecurityBuilder AddTokenStore<TStore>()
        where TStore : class, ISessionStore, IClaimsJournal, ITokenStore
    {
        Builder.Services.AddSingleton<ISessionStore, TStore>();
        Builder.Services.AddSingleton<IClaimsJournal, TStore>();
        Builder.Services.AddSingleton<ITokenStore, TStore>();
        return this;
    }

    /// <summary>附加认证方案。</summary>
    public SecurityBuilder AddScheme<TOption>(
        string schemeName, bool includeInDefaultPolicy, Action<TOption>? configureOptions)
        where TOption : AuthenticationSchemeOptions, new()
    {
        Builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DummySchemeHandler>(schemeName, null);
        return this;
    }

    private sealed class DummySchemeHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>附加授权策略。</summary>
    public SecurityBuilder AddPolicy(string name, Action<AuthorizationPolicyBuilder> configure)
    {
        Builder.Services.AddAuthorization(o => o.AddPolicy(name, configure));
        return this;
    }

    /// <summary>凭据验证器注册(自定义登录验证扩展点)。</summary>
    public SecurityBuilder AddCredentialValidator<T>() where T : class
    {
        Builder.Services.AddSingleton(typeof(T));
        return this;
    }

    /// <summary>Claims 构建器注册。</summary>
    public SecurityBuilder AddClaimsBuilder<T>() where T : class
    {
        Builder.Services.AddSingleton(typeof(T));
        return this;
    }

    /// <summary>Token 验证器注册。</summary>
    public SecurityBuilder AddTokenValidator<T>() where T : class
    {
        Builder.Services.AddSingleton(typeof(T));
        return this;
    }
}

public static class SecurityBuilderExtensions
{
    /// <summary>装配认证(注册认证服务后执行回调):AddSecurity 必须在 AddNexMeshHost 回调内调用。
    /// 配置了 NexMesh:Authentication:Redis 连接串时自动注册 Redis 认证存储(多实例共享会话)。</summary>
    public static NexMeshBuilder AddSecurity(this NexMeshBuilder builder, Action<SecurityBuilder> configure)
    {
        builder.Services.AddNexMeshAuth(builder.Configuration);
        configure(new SecurityBuilder(builder));

        var redisSection = builder.Configuration.GetSection("NexMesh:Authentication:Redis");
        var connectionString = redisSection["ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddNexMeshRedisAuthStore(connectionString, redisSection["KeyPrefix"] ?? "nexmesh:auth:");
        }
        return builder;
    }
}
