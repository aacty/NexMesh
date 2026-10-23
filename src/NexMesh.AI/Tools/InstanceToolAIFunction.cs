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

    private static string TrimAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length + 1
            ? name[..^"Async".Length]
            : name;

    private static object? DefaultOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
}
