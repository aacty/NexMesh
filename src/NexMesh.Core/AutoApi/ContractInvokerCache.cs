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

    /// <summary>参数元数据快照:GetParameters 的数组分配与元数据遍历从每请求收编为首调一次(调用方只读使用)。</summary>
    private static readonly ConcurrentDictionary<MethodInfo, ParameterInfo[]> Parameters = new();

    /// <summary>[FromForm] 探测缓存:GetCustomAttribute 每次重新实例化特性(实测 1595ns/528B),
    /// 每复杂参数每请求探测改为首调一次——元数据对象运行期恒定,缓存即永久。
    /// 注意:探测的是 **NexMesh.FromFormAttribute**(框架自有标记,namespace 就近规则下
    /// AutoApiDispatcher 历史短名解析即此特性),不是 Mvc 同名特性。</summary>
    private static readonly ConcurrentDictionary<ParameterInfo, bool> FromFormFlags = new();

    /// <summary>集合参数探测缓存:MakeGenericType 每次分配新封闭类型(实测 96B/次),按 Type 缓存探测结论。</summary>
    private static readonly ConcurrentDictionary<Type, (bool Is, Type? Element, Type? Concrete)> CollectionProbes = new();

    /// <summary>参数是否标注 [FromForm](缓存;NexMesh 自有标记特性)。</summary>
    public static bool HasFromForm(ParameterInfo parameter)
        => FromFormFlags.GetOrAdd(parameter, static p => p.GetCustomAttribute<FromFormAttribute>() is not null);

    /// <summary>简单元素集合探测结论(缓存;非集合返回 Is=false,Element/Concrete 为 null)。
    /// 调 AutoApiDispatcher.IsSimpleCollection 原始探测(该处自身不再回调本缓存,无环)。</summary>
    public static (bool Is, Type? Element, Type? Concrete) ProbeSimpleCollection(Type type)
        => CollectionProbes.GetOrAdd(type, static t =>
            AutoApiDispatcher.IsSimpleCollection(t, out var element, out var concrete)
                ? (true, element, concrete)
                : (false, null, null));

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

    /// <summary>参数元数据快照(只读);替代热路径每请求 method.GetParameters()。</summary>
    public static ParameterInfo[] GetParameters(MethodInfo method)
        => Parameters.GetOrAdd(method, static m => m.GetParameters());

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
