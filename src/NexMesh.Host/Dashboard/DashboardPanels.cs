namespace NexMesh.Host.Dashboard;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Dashboard 面板抽象:每能力一个实现,探测容器决定 enabled 并产出面板数据。
/// 加面板 = 加一个实现并在 DashboardPanels 聚合注册——前端按返回的 panels 数组数据驱动渲染,零前端改动。
/// </summary>
internal interface IDashboardPanel
{
    /// <summary>面板标识(前端渲染键)。</summary>
    string Id { get; }

    /// <summary>面板标题。</summary>
    string Title { get; }

    /// <summary>探测并产出面板数据;未启用返回 enabled=false。</summary>
    Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default);
}

/// <summary>启动时刻记录(运行时长计算;Dashboard 首次映射时落单例)。</summary>
internal sealed class DashboardStartupClock
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>面板聚合:固定顺序输出全部面板(未启用的以 enabled=false 出现,前端不渲染标签)。</summary>
internal static class DashboardPanels
{
    public static List<IDashboardPanel> All() =>
    [
        new OverviewPanel(),
        new AiPanel(),
        new ApprovalsPanel(),
        new JobsPanel(),
        new EventsPanel(),
        new MigrationsPanel(),
        new CachePanel(),
        new SessionsPanel(),
    ];
}

/// <summary>概览面板(常驻):环境/框架版本/运行时长/端点数/模块清单。</summary>
internal sealed class OverviewPanel : IDashboardPanel
{
    public string Id => "overview";
    public string Title => "概览";

    public Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var env = services.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var endpoints = services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var clock = services.GetService<DashboardStartupClock>();
        var modules = services.GetService<NexMesh.Core.HostAssemblySet>()?.Modules?
            .Select(m => m.GetType().Name)
            .Order()
            .ToList();

        var hostAssembly = typeof(NexMeshHostExtensions).Assembly;
        var version = hostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? hostAssembly.GetName().Version?.ToString() ?? "-";

        return Task.FromResult(new Dictionary<string, object?>
        {
            ["environment"] = env?.EnvironmentName ?? "-",
            ["frameworkVersion"] = version,
            ["uptimeSeconds"] = clock is null ? 0 : (long)(DateTimeOffset.UtcNow - clock.StartedAt).TotalSeconds,
            ["endpointCount"] = endpoints.Endpoints.Count,
            ["modules"] = modules,
        });
    }
}

/// <summary>AI 面板:Provider 清单(不含 ApiKey)+ 工具清单(含 Risk 徽章,反射扫 [NexMeshTool])。</summary>
internal sealed class AiPanel : IDashboardPanel
{
    public string Id => "ai";
    public string Title => "AI";

    public Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var registry = services.GetService<NexMesh.AI.Tools.INexMeshToolRegistry>();
        if (registry is null)
        {
            return Task.FromResult(Disabled());
        }

        var options = services.GetService<IOptions<NexMesh.AI.Configuration.NexMeshAiOptions>>()?.Value;
        var providers = (options?.Providers ?? [])
            .Select(kv => new Dictionary<string, object?>
            {
                ["name"] = kv.Key,
                ["type"] = kv.Value.Type,
                ["model"] = kv.Value.Model, // 不回 ApiKey/Endpoint
            })
            .ToList();

        // Risk 不在 AIFunction 上:从扫描程序集的 [NexMeshTool] 反射补全
        var risks = CollectToolRisks(services);
        var tools = registry.DiscoverTools()
            .Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["risk"] = risks.TryGetValue(t.Name, out var risk) ? risk : "Low",
            })
            .ToList();

        return Task.FromResult(new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["defaultProvider"] = options?.DefaultProvider ?? "-",
            ["providers"] = providers,
            ["tools"] = tools,
        });
    }

    private static Dictionary<string, object?> Disabled() => new() { ["enabled"] = false };

    private static Dictionary<string, string> CollectToolRisks(IServiceProvider services)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assemblies = services.GetService<NexMesh.Core.HostAssemblySet>()?.Assemblies
                         ?? [Assembly.GetEntryAssembly()!];
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var attr = method.GetCustomAttribute<NexMesh.AI.Tools.NexMeshToolAttribute>();
                    if (attr is not null)
                    {
                        // AIFunction 名去 Async 后缀(InstanceToolAIFunction.TrimAsyncSuffix):两种键都登记,查表必命中
                        // (实战审查发现:仅按方法名登记时 High 工具在面板显示为 Low 默认值)
                        result[method.Name] = attr.Risk.ToString();
                        var trimmed = method.Name.EndsWith("Async", StringComparison.Ordinal)
                            ? method.Name[..^5]
                            : method.Name + "Async";
                        result[trimmed] = attr.Risk.ToString();
                    }
                }
            }
        }
        return result;
    }
}

/// <summary>审批中心面板:待审清单(操作走独立 approvals 端点,此处只报状态)。</summary>
internal sealed class ApprovalsPanel : IDashboardPanel
{
    public string Id => "approvals";
    public string Title => "审批中心";

    public async Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var store = services.GetService<NexMesh.AI.Approval.IApprovalStore>();
        if (store is null)
        {
            return new Dictionary<string, object?> { ["enabled"] = false };
        }
        var pending = await store.ListPendingAsync();
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["pendingCount"] = pending.Count,
            ["pending"] = pending.Select(p => new Dictionary<string, object?>
            {
                ["id"] = p.Id,
                ["toolName"] = p.ToolName,
                ["description"] = p.Description,
                ["createdAt"] = p.CreatedAt,
            }).ToList(),
        };
    }
}

/// <summary>后台作业面板:IHostedService 实例中 NexMeshCronJob/IntervalJob 派生(JobName 公开属性)。</summary>
internal sealed class JobsPanel : IDashboardPanel
{
    public string Id => "jobs";
    public string Title => "后台作业";

    public Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var hosted = services.GetServices<IHostedService>();
        var jobs = new List<Dictionary<string, object?>>();
        foreach (var service in hosted)
        {
            var type = service.GetType();
            var isCron = IsDerived(type, "NexMesh.Worker.NexMeshCronJob");
            var isInterval = IsDerived(type, "NexMesh.Worker.NexMeshIntervalJob");
            if (!isCron && !isInterval)
            {
                continue;
            }
            jobs.Add(new Dictionary<string, object?>
            {
                ["jobName"] = type.GetProperty("JobName")?.GetValue(service) as string ?? type.Name,
                ["type"] = type.Name,
                ["kind"] = isCron ? "Cron" : "Interval",
                ["runs"] = MapRuns(type.GetProperty("RecentRuns")?.GetValue(service)),
            });
        }
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["enabled"] = jobs.Count > 0,
            ["jobs"] = jobs,
        });
    }

    /// <summary>反射映射 RecentRuns(不引 Worker 包:作业面板与可选能力保持松耦合)。</summary>
    private static List<Dictionary<string, object?>> MapRuns(object? runs)
    {
        if (runs is not System.Collections.IEnumerable enumerable)
        {
            return [];
        }
        var result = new List<Dictionary<string, object?>>();
        foreach (var run in enumerable)
        {
            var t = run.GetType();
            result.Add(new Dictionary<string, object?>
            {
                ["startedAt"] = t.GetProperty("StartedAt")?.GetValue(run),
                ["durationMs"] = t.GetProperty("DurationMs")?.GetValue(run),
                ["ok"] = t.GetProperty("Ok")?.GetValue(run),
                ["error"] = t.GetProperty("Error")?.GetValue(run),
                ["trigger"] = t.GetProperty("Trigger")?.GetValue(run),
                ["triggeredBy"] = t.GetProperty("TriggeredBy")?.GetValue(run),
                ["triggeredBy"] = t.GetProperty("TriggeredBy")?.GetValue(run),
            });
        }
        return result;
    }

    private static bool IsDerived(Type type, string baseTypeName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == baseTypeName)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>事件总线面板:配置 + 队列深度(Channel Reader.Count)。</summary>
internal sealed class EventsPanel : IDashboardPanel
{
    public string Id => "events";
    public string Title => "事件总线";

    public Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var publisher = services.GetService<NexMesh.Events.IEventPublisher>();
        if (publisher is null)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["enabled"] = false });
        }
        var options = services.GetService<IOptions<NexMesh.Events.NexMeshEventsOptions>>()?.Value ?? new NexMesh.Events.NexMeshEventsOptions();
        var channel = services.GetService<System.Threading.Channels.Channel<NexMesh.Events.EventWorkItem>>();
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["capacity"] = options.Capacity,
            ["concurrency"] = options.Concurrency,
            ["drainTimeoutSeconds"] = options.DrainTimeoutSeconds,
            ["queueDepth"] = channel?.Reader.Count ?? 0,
        });
    }
}

/// <summary>迁移面板:已应用迁移清单(IMigrationRunner.ListAppliedAsync;未注册迁移时 enabled=false)。</summary>
internal sealed class MigrationsPanel : IDashboardPanel
{
    public string Id => "migrations";
    public string Title => "数据库迁移";

    public async Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var runner = services.GetService<NexMesh.Data.Migrations.IMigrationRunner>();
        if (runner is null)
        {
            return new Dictionary<string, object?> { ["enabled"] = false };
        }
        var applied = await runner.ListAppliedAsync(ct);
        var pending = await runner.ListPendingAsync(ct);
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["count"] = applied.Count,
            ["pendingCount"] = pending.Count,
            ["pending"] = pending.Select(m => new Dictionary<string, object?>
            {
                ["source"] = m.Source,
                ["version"] = m.Version,
                ["name"] = m.Name,
            }).ToList(),
            ["migrations"] = applied.Select(m => new Dictionary<string, object?>
            {
                ["source"] = m.Source,
                ["version"] = m.Version,
                ["name"] = m.Name,
                ["appliedAt"] = m.AppliedAt,
                ["durationMs"] = m.DurationMs,
            }).ToList(),
        };
    }
}

/// <summary>缓存面板:模式与源清单(经配置节读取——ICacheFactory 无枚举接口,零新契约)。</summary>
internal sealed class CachePanel : IDashboardPanel
{
    public string Id => "cache";
    public string Title => "缓存";

    public Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService<NexMesh.Cache.ICacheFactory>() is null)
        {
            return Task.FromResult(new Dictionary<string, object?> { ["enabled"] = false });
        }
        var config = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var mode = config?["NexMesh:Cache:Mode"] ?? "Auto";
        var sources = new List<Dictionary<string, object?>>();
        var section = config?.GetSection("NexMesh:Cache:Sources");
        if (section is not null)
        {
            foreach (var name in section.GetChildren())
            {
                sources.Add(new Dictionary<string, object?>
                {
                    ["name"] = name.Key,
                    ["keyPrefix"] = name["KeyPrefix"] ?? "nexmesh:",
                    ["defaultTtlSeconds"] = name["DefaultTtlSeconds"] ?? "120",
                    ["l1TtlSeconds"] = name["L1TtlSeconds"] ?? "10",
                    // ConnectionString 不回显(可能含凭据)
                });
            }
        }
        if (sources.Count == 0)
        {
            sources.Add(new Dictionary<string, object?> { ["name"] = "main", ["keyPrefix"] = "nexmesh:", ["defaultTtlSeconds"] = "120", ["l1TtlSeconds"] = "10" });
        }
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["mode"] = mode,
            ["sources"] = sources,
        });
    }
}

/// <summary>会话面板:活跃会话清单(ISessionStore.ListActiveAsync;Token 仅前缀,不回明文)。</summary>
internal sealed class SessionsPanel : IDashboardPanel
{
    public string Id => "sessions";
    public string Title => "会话";

    public async Task<Dictionary<string, object?>> BuildAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var store = services.GetService<NexMesh.Auth.ISessionStore>();
        if (store is null)
        {
            return new Dictionary<string, object?> { ["enabled"] = false };
        }
        var sessions = await store.ListActiveAsync(ct);
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["count"] = sessions.Count,
            ["sessions"] = sessions.Select(s => new Dictionary<string, object?>
            {
                ["tokenPrefix"] = s.TokenPrefix,
                ["userId"] = s.UserId,
                ["userName"] = s.UserName,
                ["tenantId"] = s.TenantId,
                ["issuedAt"] = s.IssuedAt,
                ["lastActivityAt"] = s.LastActivityAt,
                ["expiresAt"] = s.ExpiresAt,
            }).ToList(),
        };
    }
}
