using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexMesh.Host;

/// <summary>
/// 框架管理台(能力探测驱动):静态页匿名可载,数据端点默认要求认证(Bearer 与 REST 同构);
/// overview 聚合全部面板(启用了什么就返回什么——AI/审批/后台作业/事件总线,未启用返回 enabled=false);
/// 审批操作转发 ApprovalDecisionService。开关:默认仅 Development;force=true 或 NexMesh:Dashboard:Enabled=true 开启。
/// </summary>
public static class NexMeshDashboardExtensions
{
    /// <summary>映射管理台端点(页面 + overview 聚合 + 审批操作)。</summary>
    public static IEndpointRouteBuilder MapNexMeshDashboard(
        this IEndpointRouteBuilder endpoints, bool requireAuthorization = true, bool force = false)
    {
        var services = endpoints.ServiceProvider;
        if (!IsEnabled(services, force))
        {
            return endpoints;
        }
        services.GetRequiredService<Dashboard.DashboardStartupClock>(); // 确保启动时钟已注册(幂等)

        // 静态页:匿名可载(数据端点各自守卫)
        endpoints.MapGet("/nexmesh/dashboard", () => Results.Text(LoadIndexHtml(), "text/html; charset=utf-8"));

        // overview:一次聚合全部面板
        var overview = endpoints.MapGet("/nexmesh/dashboard/overview", async (HttpContext http, CancellationToken ct) =>
        {
            var panels = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var panel in Dashboard.DashboardPanels.All())
            {
                try
                {
                    panels[panel.Id] = await panel.BuildAsync(http.RequestServices, ct);
                }
                catch (Exception)
                {
                    panels[panel.Id] = new Dictionary<string, object?> { ["enabled"] = false, ["error"] = "面板数据不可用" };
                }
            }
            return Results.Ok(ApiResult<object?>.Ok(panels));
        });
        WithAuth(overview);

        // 审批中心:数据与操作(未注册审批门时优雅降级)
        var approvals = endpoints.MapGet("/nexmesh/dashboard/approvals", async (HttpContext http, CancellationToken ct) =>
        {
            var store = http.RequestServices.GetService<NexMesh.AI.Approval.IApprovalStore>();
            if (store is null)
            {
                return Results.Ok(ApiResult<object?>.Ok(new { enabled = false, message = "审批门未启用(AddNexMeshApproval)" }));
            }
            var pending = await store.ListPendingAsync();
            return Results.Ok(ApiResult<object?>.Ok(new { enabled = true, pending }));
        });
        WithAuth(approvals);

        var approve = endpoints.MapPost("/nexmesh/dashboard/approvals/{id}/approve", async (
            string id, HttpContext http, CancellationToken ct) =>
            await DecideAsync(http, id, approve: true, ct));
        var reject = endpoints.MapPost("/nexmesh/dashboard/approvals/{id}/reject", async (
            string id, HttpContext http, CancellationToken ct) =>
            await DecideAsync(http, id, approve: false, ct));
        WithAuth(approve);
        WithAuth(reject);

        // ── 操作面:后台作业立即执行 / 会话踢下线 ─────────────────────────
        var jobTrigger = endpoints.MapPost("/nexmesh/dashboard/jobs/{jobName}/trigger", async (
            string jobName, HttpContext http) =>
        {
            var hostedJobs = http.RequestServices.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .Where(h => h.GetType().GetProperty("JobName") is not null)
                .ToList();
            // 解析顺序:①类型全名精确命中(全名天然唯一)②JobName 短名唯一命中 ③短名多命中→报歧义
            object? job = hostedJobs.FirstOrDefault(h => h.GetType().FullName == jobName);
            if (job is null)
            {
                var byName = hostedJobs.Where(h =>
                    (string?)h.GetType().GetProperty("JobName")?.GetValue(h) == jobName).ToList();
                if (byName.Count == 1)
                {
                    job = byName[0];
                }
                else if (byName.Count > 1)
                {
                    return Results.Ok(ApiResult<object?>.Fail(
                        $"同名作业 {byName.Count} 个({string.Join(", ", byName.Select(h => h.GetType().FullName))}),请用类型全名触发", 400));
                }
            }
            if (job is null)
            {
                return Results.Ok(ApiResult<object?>.Fail($"未找到作业 {jobName}", 404));
            }
            // 反射取 TriggerNowAsync 执行(Worker 公共面;job 执行体走其内部闸)
            var trigger = job.GetType().GetMethod("TriggerNowAsync")
                ?? throw new InvalidOperationException($"作业 {jobName} 不支持手动触发");
            try
            {
                // 审计:操作人随记录(HTTP 访问日志为权威,此为面板速览)
                var operatorName = http.RequestServices.GetService<NexMesh.Core.ICurrentUser>()?.UserName
                    ?? http.RequestServices.GetService<NexMesh.Core.ICurrentUser>()?.UserId;
                var task = (Task<bool>)trigger.Invoke(job, [operatorName, CancellationToken.None])!;
                var started = await task;
                return Results.Ok(ApiResult<object?>.Ok(new { triggered = started, message = started ? "已执行" : "作业正在执行中,请稍后再试" }));
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // 作业执行失败:降级信封(业务异常不外抛 500)
                return Results.Ok(ApiResult<object?>.Fail($"执行失败:{tie.InnerException.Message}", 500));
            }
            catch (Exception ex)
            {
                return Results.Ok(ApiResult<object?>.Fail($"执行失败:{ex.Message}", 500));
            }
        });
        WithAuth(jobTrigger);

        // ── 缓存键巡视(只读列表 + 单键删除;经 ICacheInspection——Redis SCAN 上限,内存模式诚实空集)──
        var cacheKeys = endpoints.MapGet("/nexmesh/dashboard/cache/{source}/keys", async (
            string source, string? prefix, HttpContext http, CancellationToken ct) =>
        {
            var factory = http.RequestServices.GetService<NexMesh.Cache.ICacheFactory>();
            if (factory is null)
            {
                return Results.Ok(ApiResult<object?>.Fail("缓存未启用(AddNexMeshCache)", 400));
            }
            var cacheSource = factory.Get(source);
            if (cacheSource is not NexMesh.Cache.ICacheInspection inspection)
            {
                return Results.Ok(ApiResult<object?>.Ok(new { keys = Array.Empty<object>() }));
            }
            var keys = await inspection.ListKeysAsync(prefix ?? "", 200, ct);
            return Results.Ok(ApiResult<object?>.Ok(keys));
        });
        WithAuth(cacheKeys);

        var cacheKeyDelete = endpoints.MapDelete("/nexmesh/dashboard/cache/{source}/keys/{*key}", async (
            string source, string key, HttpContext http, CancellationToken ct) =>
        {
            var factory = http.RequestServices.GetService<NexMesh.Cache.ICacheFactory>();
            if (factory?.Get(source) is not NexMesh.Cache.ICacheInspection inspection)
            {
                return Results.Ok(ApiResult<object?>.Fail("该缓存源不支持键操作", 400));
            }
            var removed = await inspection.RemoveKeyAsync(key, ct);
            return Results.Ok(ApiResult<object?>.Ok(new { removed }));
        });
        WithAuth(cacheKeyDelete);

        // 迁移在线触发:经 IMigrationRunner.RunPendingAsync(内含分布式锁+进程闸+单迁移事务+fail-fast;
        // 与部署期执行完全同一路径——多实例下其他实例的运行中迁移会被锁挡住,不双跑)
        var migrateRun = endpoints.MapPost("/nexmesh/dashboard/migrations/run", async (HttpContext http, CancellationToken ct) =>
        {
            var runner = http.RequestServices.GetService<NexMesh.Data.Migrations.IMigrationRunner>();
            if (runner is null)
            {
                return Results.Ok(ApiResult<object?>.Fail("迁移未启用(AddNexMeshMigrations)", 400));
            }
            var applied = await runner.RunPendingAsync(ct);
            return Results.Ok(ApiResult<object?>.Ok(new { applied }));
        });
        WithAuth(migrateRun);

        var sessionRevoke = endpoints.MapPost("/nexmesh/dashboard/sessions/{userId}/revoke", async (
            string userId, HttpContext http, CancellationToken ct) =>
        {
            var store = http.RequestServices.GetService<NexMesh.Auth.ISessionStore>();
            if (store is null)
            {
                return Results.Ok(ApiResult<object?>.Fail("认证会话未启用", 400));
            }
            var revoked = await store.RevokeByUserAsync(userId, ct);
            return Results.Ok(ApiResult<object?>.Ok(new { revoked }));
        });
        WithAuth(sessionRevoke);

        return endpoints;

        void WithAuth(IEndpointConventionBuilder builder)
        {
            if (requireAuthorization)
            {
                builder.RequireAuthorization();
            }
        }

        static async Task<IResult> DecideAsync(HttpContext http, string id, bool approve, CancellationToken ct)
        {
            var decisions = http.RequestServices.GetService<NexMesh.AI.Approval.ApprovalDecisionService>();
            if (decisions is null)
            {
                return Results.Ok(ApiResult<object?>.Fail("审批门未启用(AddNexMeshApproval)", 400));
            }
            var user = http.RequestServices.GetService<NexMesh.Core.ICurrentUser>();
            var decidedBy = user?.UserId ?? "operator";
            var request = approve
                ? await decisions.ApproveAsync(id, decidedBy)
                : await decisions.RejectAsync(id, decidedBy);
            // 投影:枚举转字符串(minimal API 默认 JsonOptions 无 JsonStringEnumConverter)
            return Results.Ok(ApiResult<object?>.Ok(new
            {
                request.Id,
                request.ToolName,
                Status = request.Status.ToString(),
                request.DecidedBy,
                request.Result,
                request.Error,
            }));
        }
    }

    /// <summary>注册启动时钟(MapNexMeshDashboard 会自动调用;AddNexMeshService/Host 不自动开管理台,业务显式 Map 才有)。</summary>
    public static IServiceCollection AddNexMeshDashboard(this IServiceCollection services)
    {
        services.TryAddSingleton<Dashboard.DashboardStartupClock>();
        return services;
    }

    private static bool IsEnabled(IServiceProvider services, bool force)
    {
        if (force)
        {
            return true;
        }
        var config = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        if (bool.TryParse(config?["NexMesh:Dashboard:Enabled"], out var enabled))
        {
            return enabled;
        }
        var env = services.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        return string.Equals(env?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadIndexHtml()
    {
        var assembly = typeof(NexMeshDashboardExtensions).Assembly;
        var resourceName = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Dashboard index.html 资源缺失");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
