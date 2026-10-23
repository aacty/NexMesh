namespace NexMesh.Core;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>模块生命周期阶段(同阶段内按 Priority 升序执行)。</summary>
public enum ModulePhase
{
    Platform,
    Framework,
    Security,
    Observability,
    Component,
    Application,
}

/// <summary>
/// 模块契约:ConfigureServices 参与服务注册(AddNexMeshHost 阶段),
/// ConfigureApplication 参与管道/端点装配(UseNexMeshHost 阶段)。
/// 业务模块继承 AppModule 即可被 AutoScanModules 自动发现。
/// </summary>
public interface INexMeshModule
{
    string Name { get; }

    ModulePhase Phase { get; }

    int Priority { get; }

    void ConfigureServices(NexMeshBuilder builder);

    void ConfigureApplication(WebApplication app);
}

/// <summary>模块基类:全部成员虚实现,业务只覆写需要的部分。</summary>
public abstract class AppModule : INexMeshModule
{
    public virtual string Name => GetType().Name;

    public virtual ModulePhase Phase => ModulePhase.Application;

    public virtual int Priority => 0;

    public virtual void ConfigureServices(NexMeshBuilder builder)
    {
    }

    public virtual void ConfigureApplication(WebApplication app)
    {
    }
}

/// <summary>宿主选项(NexMesh 配置节:IP/HttpPort/GrpcPort/AutoScanModules)。</summary>
public sealed class NexMeshHostOptions
{
    public string IP { get; set; } = "";

    public int HttpPort { get; set; }

    public int GrpcPort { get; set; }

    public bool AutoScanModules { get; set; } = true;

    public bool EnableModuleLifecycleLogging { get; set; }
}

/// <summary>
/// 模块宿主构建器:服务注册 + 配置 + 模块管理。
/// BindOptions 绑定配置节(与 IOptions 同源);AddModule 显式挂载;AutoScanModules 按程序集闭包扫描。
/// </summary>
public sealed class NexMeshBuilder(IServiceCollection services, IConfiguration configuration)
{
    public IServiceCollection Services { get; } = services;

    public IConfiguration Configuration { get; } = configuration;

    public NexMeshHostOptions Options { get; } = new();

    public List<INexMeshModule> Modules { get; } = [];

    /// <summary>挂载模块(单例实例)。</summary>
    public NexMeshBuilder AddModule(INexMeshModule module)
    {
        Modules.Add(module);
        return this;
    }

    /// <summary>挂载模块(无参构造实例)。</summary>
    public NexMeshBuilder AddModule<TModule>() where TModule : INexMeshModule, new()
        => AddModule(new TModule());

    /// <summary>绑定配置节为强类型选项并注册 IOptions(返回强类型便于模块内直接使用)。</summary>
    public TOptions BindOptions<TOptions>(string sectionPath) where TOptions : class, new()
    {
        var options = new TOptions();
        Configuration.GetSection(sectionPath).Bind(options);
        Services.Configure<TOptions>(Configuration.GetSection(sectionPath));
        return options;
    }

    /// <summary>扫描程序集闭包中全部 AppModule 子类并挂载(入口程序集为根)。</summary>
    public NexMeshBuilder AutoScanModules(params Assembly[] assemblies)
    {
        foreach (var asm in ModuleHostAssemblyScanner.Closure(assemblies))
        {
            foreach (var type in SafeGetTypes(asm))
            {
                if (type is { IsClass: true, IsAbstract: false }
                    && typeof(AppModule).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    if (Activator.CreateInstance(type) is INexMeshModule module
                        && Modules.All(m => m.GetType() != type))
                    {
                        Modules.Add(module);
                    }
                }
            }
        }
        return this;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

/// <summary>程序集闭包扫描:入口程序集 + 引用的非系统程序集(传递闭包)。</summary>
public static class ModuleHostAssemblyScanner
{
    public static Assembly[] Closure(Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];
        var result = new HashSet<Assembly>();
        var pending = new Queue<Assembly>(assemblies);
        while (pending.Count > 0)
        {
            var asm = pending.Dequeue();
            if (!result.Add(asm))
            {
                continue;
            }
            foreach (var reference in asm.GetReferencedAssemblies())
            {
                if (reference.Name is null
                    || reference.Name.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || reference.Name.StartsWith("System.", StringComparison.Ordinal)
                    || reference.Name.StartsWith("NexMesh.", StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // 未加载的引用程序集(按需加载的库):跳过
                }
            }
        }
        return [.. result];
    }
}

/// <summary>宿主扫描结果载体(AddNexMeshHost 存、UseNexMeshHost 取)。</summary>
public sealed record HostAssemblySet(Assembly[] Assemblies)
{
    public List<INexMeshModule> Modules { get; init; } = [];
}
