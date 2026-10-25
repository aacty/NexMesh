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
        services.AddSingleton(new ContractGrpcRegistry(
            assemblies.Length > 0 ? assemblies : [System.Reflection.Assembly.GetEntryAssembly()!]));
        return services;
    }

    /// <summary>映射契约 gRPC 网关(AddNexMeshGrpc 之后)。</summary>
    public static IEndpointRouteBuilder MapNexMeshGrpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<ContractGatewayService>();
        return endpoints;
    }

    /// <summary>契约程序集载体(网关服务构造注入);惰性缓存 (service,method)→端点 映射,避免每请求全量反射扫描。</summary>
    public sealed class ContractGrpcRegistry(System.Reflection.Assembly[] assemblies)
    {
        public System.Reflection.Assembly[] Assemblies { get; } = assemblies;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AutoApiEndpoint?> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>按 (service, method) 查端点;首扫构建,后续 O(1);未知方法(null)同样缓存不重复扫。</summary>
        public AutoApiEndpoint? Find(string service, string method)
            => _cache.GetOrAdd($"{service}.{method}", _ =>
                ContractApiExtensions.ScanContractEndpoints(Assemblies).FirstOrDefault(e =>
                    RouteService(e.Route).Equals(service, StringComparison.OrdinalIgnoreCase)
                    && MethodName(e).Equals(method, StringComparison.OrdinalIgnoreCase)));

        internal static string RouteService(string route) => route.Split('/')[1].ToLowerInvariant();

        internal static string MethodName(AutoApiEndpoint e) =>
            e.Method.Name.EndsWith("Async", StringComparison.Ordinal) ? e.Method.Name[..^5] : e.Method.Name;
    }

    /// <summary>网关服务:Invoke(service, method, args_json) → 契约实现 → ApiResult 信封。</summary>
    public sealed class ContractGatewayService(
        ContractGrpcRegistry registry,
        ILogger<ContractGatewayService>? logger = null) : ContractGatewayBase
    {
        public override async Task<NexMesh.Grpc.ContractReply> Invoke(
            NexMesh.Grpc.ContractCall request,
            GrpcCore.ServerCallContext context)
        {
            var (endpoint, error) = Resolve(request.Service, request.Method);
            if (error is not null)
            {
                return Reply(Fail(error));
            }
            try
            {
                var args = BindArgs(endpoint!, request.ArgsJson, context.CancellationToken);
                var service = global::Grpc.Core.ServerCallContextExtensions.GetHttpContext(context).RequestServices.GetRequiredService(endpoint!.ServiceType);
                var result = ContractInvokerCache.GetInvoker(endpoint.Method)(service, args);
                var payload = await UnwrapAsync(result);
                var envelope = ApiResult<object?>.Ok(payload);
                envelope.TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
                return Reply(JsonSerializer.Serialize(envelope, Json));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[NexMesh.gRPC] Invoke {Service}.{Method} 失败", request.Service, request.Method);
                return Reply(Fail(ex is NexMesh.MeshException me ? me.Message : "服务内部错误"));
            }
        }

        private (AutoApiEndpoint?, string?) Resolve(string service, string method)
        {
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(method))
            {
                return (null, "service 与 method 不能为空");
            }
            var target = registry.Find(service, method); // 惰性缓存:首扫后 O(1)
            return target is null ? (null, $"未找到契约方法 {service}.{method}") : (target, null);
        }

        private static object?[] BindArgs(AutoApiEndpoint endpoint, string? argsJson, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
            var root = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default;

            var parameters = endpoint.Method.GetParameters();
            var invokeArgs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType == typeof(CancellationToken))
                {
                    invokeArgs[i] = cancellationToken;
                    continue;
                }
                if (root.ValueKind == JsonValueKind.Undefined || !root.TryGetProperty(p.Name!, out var value))
                {
                    invokeArgs[i] = p.HasDefaultValue ? p.DefaultValue : null;
                    continue;
                }
                invokeArgs[i] = value.ValueKind == JsonValueKind.Null
                    ? null
                    : value.Deserialize(p.ParameterType, Json);
            }
            return invokeArgs;
        }

        private static async Task<object?> UnwrapAsync(object? result)
        {
            if (result is Task task)
            {
                await task;
                return task.GetType().GetProperty("Result")?.GetValue(task);
            }
            return result;
        }

        private static NexMesh.Grpc.ContractReply Reply(string json) => new() { EnvelopeJson = json };

        private static string Fail(string message) =>
            JsonSerializer.Serialize(ApiResult<object?>.Fail(message, 400), Json);
    }
}
