using System.Text.Json;
using GrpcCore = Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexMesh.Core.AutoApi;
using static NexMesh.Grpc.ContractGateway;

namespace NexMesh.Host;

/// <summary>
/// 契约双面输出的 gRPC 面(通用网关模式):业务 IServiceApi 契约零生成,
/// 经单一 unary 方法按 (service, method) 路由到与 REST 完全相同的契约实现。
/// args_json = {"参数名":值}(简单类型直接转换,复杂类型 JSON 反序列化);返回 REST 同款 ApiResult 信封 JSON。
/// Bearer 认证 header 与 REST 同构(认证中间件前置);建议部署于内网(端点级授权差异由 REST 面承担)。
/// </summary>
public static class NexMeshGrpcExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>注册 gRPC 契约网关(AddGrpc + 契约程序集,供 MapNexMeshGrpc 挂载)。</summary>
    public static IServiceCollection AddNexMeshGrpc(
        this IServiceCollection services,
        params System.Reflection.Assembly[] assemblies)
    {
        services.AddGrpc();
        services.AddGrpcReflection(); // 客户端动态发现(GrpcReflectionService)
        services.AddSingleton(new ContractGrpcRegistry(
            assemblies.Length > 0 ? assemblies : [System.Reflection.Assembly.GetEntryAssembly()!]));
        return services;
    }

    /// <summary>映射契约 gRPC 网关(AddNexMeshGrpc 之后)。</summary>
    public static IEndpointRouteBuilder MapNexMeshGrpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<ContractGatewayService>();
        endpoints.MapGrpcReflectionService(); // gRPC Reflection:客户端可动态发现服务/方法
        return endpoints;
    }

    /// <summary>契约程序集载体(网关服务构造注入)。(service,method)→端点索引一次性构建(此前每个新键触发全量重扫,首 M 次调用 O(M²));
    /// 每端点调用计划(参数元数据+编译委托)按需创建并缓存——调用路径零反射。</summary>
    public sealed class ContractGrpcRegistry(System.Reflection.Assembly[] assemblies)
    {
        public System.Reflection.Assembly[] Assemblies { get; } = assemblies;

        // SSS+ 审核修复:volatile 发布(弱内存模型下锁外读者可见完整构建的字典)+ 私有锁对象(公共类锁 this 可被外部劫持)
        private volatile Dictionary<string, AutoApiEndpoint>? _index;
        private readonly object _gate = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<AutoApiEndpoint, GatewayPlan> _plans = new();

        /// <summary>按 (service, method) 查调用计划;未命中返回 null(索引缺失即未知方法,无需另行缓存)。</summary>
        public GatewayPlan? Find(string service, string method)
        {
            var index = _index ?? BuildIndex();
            return index.TryGetValue($"{service}.{method}", out var endpoint) && endpoint is not null
                ? _plans.GetOrAdd(endpoint, CreatePlan)
                : null;
        }

        private Dictionary<string, AutoApiEndpoint> BuildIndex()
        {
            lock (_gate)
            {
                if (_index is { } built)
                {
                    return built;
                }
                var index = new Dictionary<string, AutoApiEndpoint>(StringComparer.OrdinalIgnoreCase);
                foreach (var endpoint in ContractApiExtensions.ScanContractEndpoints(Assemblies))
                {
                    // 重复 (service,method) 首个胜出(与历史 FirstOrDefault 语义一致)
                    index.TryAdd($"{RouteService(endpoint.Route)}.{MethodName(endpoint)}", endpoint);
                }
                _index = index;
                return index;
            }
        }

        internal static string RouteService(string route) => route.Split('/')[1].ToLowerInvariant();

        internal static string MethodName(AutoApiEndpoint e) =>
            e.Method.Name.EndsWith("Async", StringComparison.Ordinal) ? e.Method.Name[..^5] : e.Method.Name;

        /// <summary>每端点调用计划:参数元数据快照(替代每请求 GetParameters)+ 编译调用器 + 编译 Task&lt;T&gt;.Result 读值器(替代每请求 GetProperty/GetValue)。</summary>
        internal static GatewayPlan CreatePlan(AutoApiEndpoint endpoint)
        {
            var parameters = endpoint.Method.GetParameters();
            var planned = new GatewayParameter[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                planned[i] = new GatewayParameter(
                    p.Name ?? "", p.ParameterType, p.HasDefaultValue, p.DefaultValue,
                    p.ParameterType == typeof(CancellationToken));
            }
            var returnType = endpoint.Method.ReturnType;
            // Task<T>:编译 Result 读值器;非泛型 Task:无读值器(返回 null,与历史 GetProperty 语义一致)
            Func<Task, object?>? taskResult = null;
            if (returnType != typeof(Task) && typeof(Task).IsAssignableFrom(returnType))
            {
                taskResult = ContractInvokerCache.GetTaskResultGetter(returnType.GetProperty("Result")!);
            }
            return new GatewayPlan(endpoint, planned, ContractInvokerCache.GetInvoker(endpoint.Method), taskResult);
        }
    }

    /// <summary>每端点预编译调用计划:热路径零反射。</summary>
    public sealed class GatewayPlan(
        AutoApiEndpoint endpoint,
        GatewayParameter[] parameters,
        Func<object, object?[], object?> invoke,
        Func<Task, object?>? taskResult)
    {
        public AutoApiEndpoint Endpoint { get; } = endpoint;
        public GatewayParameter[] Parameters { get; } = parameters;
        public Func<object, object?[], object?> Invoke { get; } = invoke;

        /// <summary>Task&lt;T&gt;.Result 编译读值器;非泛型 Task 为 null。</summary>
        public Func<Task, object?>? TaskResult { get; } = taskResult;
    }

    /// <summary>契约方法参数计划快照(名称/类型/缺省值/取消令牌位)。</summary>
    public sealed record GatewayParameter(string Name, Type Type, bool HasDefault, object? Default, bool IsCancellationToken);

    /// <summary>网关服务:Invoke(service, method, args_json) → 契约实现 → ApiResult 信封。</summary>
    public sealed class ContractGatewayService(
        ContractGrpcRegistry registry,
        ILogger<ContractGatewayService>? logger = null) : ContractGatewayBase
    {
        public override async Task<NexMesh.Grpc.ContractReply> Invoke(
            NexMesh.Grpc.ContractCall request,
            GrpcCore.ServerCallContext context)
        {
            var plan = Resolve(request.Service, request.Method);
            if (plan is null)
            {
                return Reply(Fail(string.IsNullOrWhiteSpace(request.Service) || string.IsNullOrWhiteSpace(request.Method)
                    ? "service 与 method 不能为空"
                    : $"未找到契约方法 {request.Service}.{request.Method}"));
            }
            try
            {
                var args = BindArgs(plan, request.ArgsJson, context.CancellationToken);
                var service = global::Grpc.Core.ServerCallContextExtensions.GetHttpContext(context).RequestServices.GetRequiredService(plan.Endpoint.ServiceType);
                var raw = plan.Invoke(service, args);
                object? payload;
                if (raw is Task task)
                {
                    await task; // 业务异常原样抛出(无 TargetInvocationException 包装)
                    payload = plan.TaskResult?.Invoke(task); // 编译读值器:Task<T>→T(装箱);非泛型 Task→null
                }
                else
                {
                    payload = raw;
                }
                var envelope = ApiResult<object?>.Ok(payload);
                envelope.TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
                return Reply(JsonSerializer.Serialize(envelope, Json));
            }
            catch (System.Text.Json.JsonException jex)
            {
                // SSS+审核:客户端恶意/非法 args_json 是输入错误——独立信封 400,不冒充服务端故障污染 Error 告警
                logger?.LogWarning(jex, "[NexMesh.gRPC] Invoke {Service}.{Method} 参数 JSON 非法", request.Service, request.Method);
                return Reply(Fail("参数 JSON 非法"));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[NexMesh.gRPC] Invoke {Service}.{Method} 失败", request.Service, request.Method);
                return Reply(Fail(ex is NexMesh.MeshException me ? me.Message : "服务内部错误"));
            }
        }

        private GatewayPlan? Resolve(string service, string method)
            => string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(method)
                ? null
                : registry.Find(service, method);

        private static object?[] BindArgs(GatewayPlan plan, string? argsJson, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
            var root = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default;

            var parameters = plan.Parameters;
            var invokeArgs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.IsCancellationToken)
                {
                    invokeArgs[i] = cancellationToken;
                    continue;
                }
                if (root.ValueKind == JsonValueKind.Undefined || !root.TryGetProperty(p.Name, out var value))
                {
                    invokeArgs[i] = p.HasDefault ? p.Default : null;
                    continue;
                }
                invokeArgs[i] = value.ValueKind == JsonValueKind.Null
                    ? null
                    : value.Deserialize(p.Type, Json);
            }
            return invokeArgs;
        }

        private static NexMesh.Grpc.ContractReply Reply(string json) => new() { EnvelopeJson = json };

        private static string Fail(string message) =>
            JsonSerializer.Serialize(ApiResult<object?>.Fail(message, 400), Json);
    }
}
