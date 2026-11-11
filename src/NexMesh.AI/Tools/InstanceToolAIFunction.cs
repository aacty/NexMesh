namespace NexMesh.AI.Tools;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 实例方法工具(一等公民形态):每次调用创建独立 DI 作用域解析业务实例——
/// 构造注入的 IFreeSql/ICurrentUser/INexMeshChatFactory 生命周期由框架托管,
/// 杜绝"单例工具表持有 Scoped 服务"的并发灾难;HttpContext 经 AsyncLocal 流动,
/// 请求上下文(ICurrentUser)在调用链内对新作用域依然可见。
/// </summary>
internal sealed class InstanceToolAIFunction(
    IServiceProvider rootProvider,
    Type serviceType,
    MethodInfo method,
    NexMeshToolAttribute attr) : AIFunction
{
    public override string Name => TrimAsyncSuffix(method.Name);

    public override string Description => attr.Description;

    // SSS+业务实测修复(AiDemo 真模型发现):基类默认 JsonSchema 是空参数对象——模型看到的
    // 工具全部"无参",真模型无法传参(实弹:DeepSeek 调 QueryOrder 因无参被框架拒绝)。
    // 按方法参数构建 JSON Schema 并按 MethodInfo 缓存(元数据恒定)。
    public override System.Text.Json.JsonElement JsonSchema => SchemaCache.GetOrAdd(method, BuildSchema);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodInfo, System.Text.Json.JsonElement> SchemaCache = new();

    private static System.Text.Json.JsonElement BuildSchema(MethodInfo method)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var p in method.GetParameters())
        {
            if (p.ParameterType == typeof(CancellationToken))
            {
                continue;
            }
            var underlying = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            var schemaType = underlying == typeof(string) || underlying.IsEnum
                    || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) || underlying == typeof(Guid)
                ? "string"
                : underlying == typeof(bool)
                    ? "boolean"
                    : underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
                        || underlying == typeof(byte) || underlying == typeof(uint) || underlying == typeof(ulong)
                        || underlying == typeof(ushort) || underlying == typeof(sbyte)
                        ? "integer"
                        : "number"; // double/float/decimal 及其余
            properties[p.Name!] = new Dictionary<string, object> { ["type"] = schemaType };
            if (!p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) is null)
            {
                required.Add(p.Name!);
            }
        }
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
        return System.Text.Json.JsonSerializer.SerializeToElement(schema);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var scope = rootProvider.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService(serviceType);

        var result = method.Invoke(instance, BindArguments(arguments, cancellationToken));
        if (result is Task task)
        {
            await task;
            // 按声明返回类型取结果(非泛型 Task 在 .NET 10 运行时是 Task<VoidTaskResult>)
            var declared = method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? method.ReturnType.GetProperty("Result")
                : null;
            return declared?.GetValue(task);
        }
        return result;
    }

    /// <summary>参数绑定:按名取值,JsonElement 反序列化,缺省取参数默认值。</summary>
    private object?[] BindArguments(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else if (arguments.TryGetValue(p.Name!, out var value) && value is not null)
            {
                args[i] = value is JsonElement element
                    ? element.Deserialize(p.ParameterType)
                    : ConvertValue(value, p.ParameterType);
            }
            else
            {
                args[i] = p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType);
            }
        }
        return args;
    }

    private static object? ConvertValue(object value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(value))
        {
            return value;
        }
        var raw = value.ToString();
        return raw is null ? DefaultOf(targetType)
            : System.ComponentModel.TypeDescriptor.GetConverter(underlying).ConvertFromInvariantString(raw);
    }

    // SSS+交叉审核(G-5):与 M.E.AI AIFunctionFactory 对齐——无条件裁 Async 后缀
    //(此前仅 >"Async".Length+1 才裁,与 Factory 对短名如 "XAsync" 的裁剪规则不一致,
    // 会导致同方法经不同注册路径得到两个不同工具名)。同名冲突由注册表 fail-fast 兜底。
    private static string TrimAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length
            ? name[..^"Async".Length]
            : name;

    private static object? DefaultOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
}
