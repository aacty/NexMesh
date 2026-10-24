namespace NexMesh.Events;

using System.Text.RegularExpressions;

/// <summary>单个订阅:处理器目标 + 启动期编译的强类型调用委托(运行期零反射)。</summary>
internal sealed class EventSubscription
{
    public EventSubscription(Type subscriberType, System.Reflection.MethodInfo method, EventSubscribeAttribute attribute)
    {
        SubscriberType = subscriberType;
        HandlerName = $"{subscriberType.Name}.{method.Name}";
        SortKey = $"{subscriberType.FullName}::{method.Name}::{attribute.EventId}";
        Attribute = attribute;
        Invoke = Compile(method);
    }

    public Type SubscriberType { get; }

    public string HandlerName { get; }

    /// <summary>确定性排序键(类型全名::方法名::事件 ID):订阅执行顺序与单播选择不依赖程序集扫描顺序。</summary>
    public string SortKey { get; }

    public EventSubscribeAttribute Attribute { get; }

    /// <summary>编译委托:(subscriber 实例, context) → Task。</summary>
    public Func<object, EventContext, Task> Invoke { get; }

    /// <summary>校验处理器签名(必须为 Task 方法且仅一个 EventContext 参数),并表达式编译为直调委托。</summary>
    private static Func<object, EventContext, Task> Compile(System.Reflection.MethodInfo method)
    {
        if (method.ReturnType != typeof(Task))
        {
            throw new InvalidOperationException(
                $"[NexMesh.Events] 处理器 {method.DeclaringType!.Name}.{method.Name} 必须返回 Task(当前 {method.ReturnType.Name})。");
        }
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(EventContext))
        {
            throw new InvalidOperationException(
                $"[NexMesh.Events] 处理器 {method.DeclaringType!.Name}.{method.Name} 必须有且仅有一个 EventContext 参数。");
        }

        var instanceParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "subscriber");
        var contextParam = System.Linq.Expressions.Expression.Parameter(typeof(EventContext), "context");
        var call = System.Linq.Expressions.Expression.Call(
            System.Linq.Expressions.Expression.Convert(instanceParam, method.DeclaringType!),
            method,
            contextParam);
        return System.Linq.Expressions.Expression
            .Lambda<Func<object, EventContext, Task>>(call, instanceParam, contextParam).Compile();
    }
}

/// <summary>
/// 订阅注册表:启动期一次性构建(精确字典 + 正则列表),构建即校验(签名非法 fail-fast);
/// 匹配列表按 SortKey 确定性排序——广播顺序与单播选择跨运行稳定,不依赖程序集扫描顺序。
/// 正则匹配带超时:灾难性回溯模式(资源类攻击/误写)只会抛 RegexMatchTimeoutException
/// 被毒事件隔离壳捕获,绝不挂死消费者线程(否则停机时 Task.WhenAll 永不完成、宿主关不掉)。
/// </summary>
internal sealed class EventSubscriptionRegistry
{
    /// <summary>正则匹配超时:足够覆盖正常模式,远小于停机容忍。</summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, List<EventSubscription>> _exact = new(StringComparer.Ordinal);
    private readonly List<(Regex Pattern, EventSubscription Subscription)> _fuzzy = [];

    public EventSubscriptionRegistry(IEnumerable<EventSubscription> subscriptions)
    {
        foreach (var subscription in subscriptions.OrderBy(s => s.SortKey, StringComparer.Ordinal))
        {
            if (subscription.Attribute.FuzzyMatch)
            {
                _fuzzy.Add((new Regex(subscription.Attribute.EventId, RegexOptions.Compiled, RegexMatchTimeout), subscription));
            }
            else
            {
                if (!_exact.TryGetValue(subscription.Attribute.EventId, out var list))
                {
                    list = [];
                    _exact[subscription.Attribute.EventId] = list;
                }
                list.Add(subscription);
            }
        }
    }

    /// <summary>匹配某事件的全部订阅(确定性顺序:精确订阅 + 正则订阅,各自按 SortKey 升序)。</summary>
    public List<EventSubscription> Match(string eventId)
    {
        List<EventSubscription> result = [];
        if (_exact.TryGetValue(eventId, out var exact))
        {
            result.AddRange(exact);
        }
        foreach (var (pattern, subscription) in _fuzzy)
        {
            if (pattern.IsMatch(eventId))
            {
                result.Add(subscription);
            }
        }
        return result;
    }
}
