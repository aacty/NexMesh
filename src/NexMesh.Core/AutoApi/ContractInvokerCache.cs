namespace NexMesh.Core.AutoApi;

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Collections.Concurrent;

/// <summary>
/// 契约调用编译缓存:把 MethodInfo / Task&lt;T&gt;.Result / List&lt;T&gt;.ToArray() 三处每请求反射
/// 替换为启动期(首次调用期)表达式编译的强类型委托,调用路径零反射。
/// 委托按 MethodInfo/PropertyInfo/Type 全局缓存(元数据对象在运行期恒定,缓存即永久)。
/// </summary>
internal static class ContractInvokerCache
{
    /// <summary>契约方法调用器:(service, args) → 返回值(Task/T/void 均装箱为 object?)。</summary>
    private static readonly ConcurrentDictionary<MethodInfo, Func<object, object?[], object?>> Invokers = new();

    /// <summary>Task&lt;T&gt;.Result 取值器:(task) → T(装箱)。替代 PropertyInfo.GetValue 反射。</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, Func<Task, object?>> TaskResultGetters = new();

    /// <summary>List&lt;T&gt;.ToArray() 转换器:(IList) → T[](装箱)。替代 GetMethod("ToArray").Invoke。</summary>
    private static readonly ConcurrentDictionary<Type, Func<IList, object>> CollectionToArray = new();

    /// <summary>
    /// 取方法调用器;含 ref/out 参数的方法无法走表达式编译,回退反射调用
    /// (契约签名约定不含 ref/out,回退仅为防御,行为与历史一致)。
    /// </summary>
    public static Func<object, object?[], object?> GetInvoker(MethodInfo method)
    {
        return Invokers.GetOrAdd(method, static m =>
        {
            if (m.ContainsGenericParameters
                || m.GetParameters().Any(p => p.ParameterType.IsByRef))
            {
                return (service, args) => m.Invoke(service, args);
            }

            var serviceParam = Expression.Parameter(typeof(object), "service");
            var argsParam = Expression.Parameter(typeof(object?[]), "args");
            var parameters = m.GetParameters();
            var callArgs = new Expression[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                callArgs[i] = Expression.Convert(
                    Expression.ArrayIndex(argsParam, Expression.Constant(i)),
                    parameters[i].ParameterType);
            }
            var call = Expression.Call(Expression.Convert(serviceParam, m.DeclaringType!), m, callArgs);
            var body = (Expression)Expression.Convert(call, typeof(object));
            return Expression.Lambda<Func<object, object?[], object?>>(body, serviceParam, argsParam).Compile();
        });
    }

    /// <summary>取 Task&lt;T&gt;.Result 读值器(Task 非泛型返回 null,与 PropertyInfo 语义一致)。</summary>
    public static Func<Task, object?> GetTaskResultGetter(PropertyInfo resultProperty)
    {
        return TaskResultGetters.GetOrAdd(resultProperty, static p =>
        {
            var taskParam = Expression.Parameter(typeof(Task), "task");
            var body = (Expression)Expression.Convert(
                Expression.Property(Expression.Convert(taskParam, p.DeclaringType!), p),
                typeof(object));
            return Expression.Lambda<Func<Task, object?>>(body, taskParam).Compile();
        });
    }

    /// <summary>取 List&lt;T&gt; → T[] 转换器(elementType 为列表元素类型)。</summary>
    public static Func<IList, object> GetCollectionToArray(Type elementType)
    {
        return CollectionToArray.GetOrAdd(elementType, static t =>
        {
            var listParam = Expression.Parameter(typeof(IList), "list");
            var body = (Expression)Expression.Convert(
                Expression.Call(
                    typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(t),
                    Expression.Convert(listParam, typeof(IEnumerable<>).MakeGenericType(t))),
                typeof(object));
            return Expression.Lambda<Func<IList, object>>(body, listParam).Compile();
        });
    }
}
