namespace NexMesh.AI.Tools;

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Observability;

/// <summary>工具风险等级:High 为高危写操作——审批门未启用时发现即阻断启动;启用后挂起拦截。</summary>
public enum NexMeshRiskLevel { Low, High }

/// <summary>工具声明特性:Description 必填且将进入模型上下文(直接决定 tool_search 召回精度)。</summary>
[AttributeUsage(AttributeTargets.Method)]
public class NexMeshToolAttribute(string description, NexMeshRiskLevel risk = NexMeshRiskLevel.Low) : Attribute
{
    public string Description { get; } = description;
    public NexMeshRiskLevel Risk { get; } = risk;
}

/// <summary>工具注册器:发现 → 安全门/审批包装 → 遥测包装 → 注册(静态与实例方法均为一等公民)。</summary>
public interface INexMeshToolRegistry
{
    IEnumerable<AIFunction> DiscoverTools();

    /// <summary>按名查找未包装的原始工具(审批门批准后执行用;挂起包装再入队会造成无限循环)。</summary>
    AIFunction? FindRawFunction(string name);
}

/// <summary>
/// 扫描程序集中 [NexMeshTool] 方法注册为 AIFunction:
/// 静态方法经委托直调;实例方法(一等公民)每次调用从独立 DI 作用域解析实例。
/// 安全铁律:High 风险工具——审批门(NexMesh:AI:Approval:Enabled=false,默认)未启用时发现即抛异常阻断启动;
/// 启用且容器注册了 IToolApprovalEnqueue(NexMesh.AI.Approval 包)时包装为挂起拦截。
/// 同名工具 fail-fast:两个 [NexMeshTool] 方法映射到同一工具名(方法同名,或去 Async 后缀后同名,
/// 或仅大小写不同——注册表按 OrdinalIgnoreCase 查找)时,注册即抛 NotSupportedException,
/// 绝不允许"后注册者静默劫持"同名工具(静默覆盖会让模型调到错误实现,且审批/遥测包装指向错误探针)。
/// MaxToolsPerRequest(NexMesh:AI:MaxToolsPerRequest,默认 10)超限截断,防千级工具 Prompt 膨胀。
/// services == null 时为验证模式:仅执行安全门与元数据校验,不产出实例工具(实例工具名占用仍登记,同名冲突在启动期即暴露)。
/// </summary>
public class NexMeshToolRegistry : INexMeshToolRegistry
{
    private readonly Assembly[] _assemblies;
    private readonly int _maxToolsPerRequest;
    private readonly IServiceProvider? _services;
    private readonly bool _approvalEnabled;
    private readonly ConcurrentDictionary<string, AIFunction> _rawFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _toolOwners = new(StringComparer.OrdinalIgnoreCase);

    public NexMeshToolRegistry(IConfiguration configuration, params Assembly[] assemblies)
        : this(configuration, services: null, assemblies)
    {
    }

    public NexMeshToolRegistry(IConfiguration configuration, IServiceProvider? services, params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];
        _maxToolsPerRequest = configuration.GetValue<int>("NexMesh:AI:MaxToolsPerRequest", 10);
        _approvalEnabled = configuration.GetValue<bool>("NexMesh:AI:Approval:Enabled", false);
        _services = services;
    }

    public AIFunction? FindRawFunction(string name) =>
        _rawFunctions.TryGetValue(name, out var fn) ? fn : null;

    public IEnumerable<AIFunction> DiscoverTools()
    {
        var functions = DiscoverAllTools().ToList();

        // 防工具数量膨胀导致 Prompt 爆炸:超出阈值确定性截断(启用 ToolSearch 语义召回时由召回层决定)
        if (functions.Count > _maxToolsPerRequest)
        {
            functions = functions.Take(_maxToolsPerRequest).ToList();
        }

        return functions;
    }

    /// <summary>
    /// 全量发现(不截断):ToolSearch 语义召回的候选全集——千级工具场景下召回必须
    /// 在全部注册工具上排序,而非"前 MaxToolsPerRequest 个"。
    /// </summary>
    public IReadOnlyList<AIFunction> DiscoverAllTools()
    {
        var functions = new List<AIFunction>();
        var logger = _services?.GetService<ILogger<NexMeshToolRegistry>>();

        foreach (var asm in _assemblies)
        {
            var methods = SafeGetTypes(asm)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => m.GetCustomAttribute<NexMeshToolAttribute>() != null);

            foreach (var m in methods)
            {
                var attr = m.GetCustomAttribute<NexMeshToolAttribute>()!;
                AIFunction? raw = null;
                if (m.IsStatic)
                {
                    raw = CreateStaticFunction(m);
                }
                else if (_services is not null)
                {
                    raw = new InstanceToolAIFunction(_services, m.DeclaringType!, m, attr);
                }
                else if (attr.Risk == NexMeshRiskLevel.High && !_approvalEnabled)
                {
                    // 验证模式同样执行安全门:审批门未启用的 High 工具必须阻断启动
                    throw SecurityGate(m);
                }

                if (raw is not null)
                {
                    RegisterToolName(raw.Name, m);
                    _rawFunctions[raw.Name] = raw;

                    if (attr.Risk == NexMeshRiskLevel.High)
                    {
                        if (!_approvalEnabled)
                        {
                            // 架构安全铁律:审批门未启用,严禁注册 High 风险工具
                            throw SecurityGate(m);
                        }
                        if (_services is null)
                        {
                            // 验证模式:安全门校验已通过,容器绑定交给运行期
                            continue;
                        }
                        var gate = _services.GetService<IToolApprovalGate>()
                            ?? throw new InvalidOperationException(
                                "[NexMesh Security Gate] 已启用审批门(NexMesh:AI:Approval:Enabled=true)," +
                                "但未注册 IToolApprovalGate——请调用 services.AddNexMeshApproval(configuration)。");
                        functions.Add(new TelemetryAIFunction(new ApprovalRequiredAIFunction(raw, gate), attr.Risk, logger));
                        continue;
                    }

                    functions.Add(new TelemetryAIFunction(raw, attr.Risk, logger));
                }
                else if (_services is null)
                {
                    // 验证模式:实例工具不产出实例,但名字占用照常登记——
                    // 同名冲突(含去 Async 后缀后同名)在启动期即暴露,而非等到运行期首次发现。
                    RegisterToolName(TrimAsyncSuffix(m.Name), m);
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// 工具名占用登记与同名 fail-fast:同一注册表实例内,两个不同方法映射到同一工具名即抛。
    /// 同一方法重复发现(同一注册表多次 DiscoverAllTools/DiscoverTools,如审批测试逐次调用)按幂等处理。
    /// 注意 AIFunctionFactory 与 InstanceToolAIFunction 均对 Task/ValueTask 方法去 "Async" 后缀,
    /// 因此 DoX 与 DoXAsync 会映射到同一工具名,同样判为冲突。
    /// </summary>
    private void RegisterToolName(string toolName, MethodInfo method)
    {
        var owner = $"{method.DeclaringType!.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name))})";
        var registered = _toolOwners.GetOrAdd(toolName, owner);
        if (!string.Equals(registered, owner, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"[NexMesh Tool Registry] Duplicate tool name '{toolName}': " +
                $"'{registered}' 与 '{owner}' 映射到同一工具名。同名工具(含去 Async 后缀后同名、仅大小写不同)禁止注册," +
                "后注册者会静默劫持同名工具——请为 [NexMeshTool] 方法改名以消除冲突。");
        }
    }

    /// <summary>与 InstanceToolAIFunction 的实例工具命名规则保持一致(验证模式登记用)。</summary>
    private static string TrimAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length + 1
            ? name[..^"Async".Length]
            : name;

    private static NotSupportedException SecurityGate(MethodInfo m) => new(
        $"[NexMesh Security Gate] Tool '{m.Name}' is marked as High risk. " +
        $"High risk tools are strictly prohibited until Approval Gates are enabled " +
        $"(NexMesh:AI:Approval:Enabled=true + services.AddNexMeshApproval).");

    /// <summary>
    /// 静态 MethodInfo → 强类型委托 → AIFunction(M.E.AI 10.x 的 MethodInfo 重载不接受 null target,
    /// 委托路径对静态方法完全确定)。
    /// </summary>
    private static AIFunction CreateStaticFunction(MethodInfo m)
    {
        var parameterTypes = m.GetParameters().Select(p => p.ParameterType).ToArray();
        var delegateType = m.ReturnType == typeof(void)
            ? Expression.GetActionType(parameterTypes)
            : Expression.GetFuncType([.. parameterTypes, m.ReturnType]);
        var del = m.CreateDelegate(delegateType);
        var attr = m.GetCustomAttribute<NexMeshToolAttribute>()!;
        return AIFunctionFactory.Create(del, new AIFunctionFactoryOptions { Description = attr.Description });
    }

    /// <summary>含动态生成类型的宿主可能部分类型加载失败:跳过不可加载类型而非崩溃。</summary>
    private static Type[] SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }
}
