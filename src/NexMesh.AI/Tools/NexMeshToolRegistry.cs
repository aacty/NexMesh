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
/// MaxToolsPerRequest(NexMesh:AI:MaxToolsPerRequest,默认 10)超限截断,防千级工具 Prompt 膨胀。
/// services == null 时为验证模式:仅执行安全门与元数据校验,不产出实例工具。
/// </summary>
public class NexMeshToolRegistry : INexMeshToolRegistry
{
    private readonly Assembly[] _assemblies;
    private readonly int _maxToolsPerRequest;
    private readonly IServiceProvider? _services;
    private readonly bool _approvalEnabled;
    private readonly ConcurrentDictionary<string, AIFunction> _rawFunctions = new(StringComparer.OrdinalIgnoreCase);

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
            }
        }

        return functions;
    }

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
