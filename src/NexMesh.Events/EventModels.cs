namespace NexMesh.Events;

using NexMesh.Core.Context;
    /// <summary>事件总线配置节(NexMesh:Events)。容量/并行度/停机排干超时——资源全部有界,拒绝无界队列。</summary>
    public sealed class NexMeshEventsOptions
    {
        public const string SectionName = "NexMesh:Events";

        /// <summary>有界队列容量;满时发布端回压等待(FullMode.Wait),不丢不静默。默认 10000。</summary>
        public int Capacity { get; set; } = 10_000;

        /// <summary>消费并行度(事件间并行,单事件内订阅者顺序执行)。默认 1(全局顺序)。</summary>
        public int Concurrency { get; set; } = 1;

        /// <summary>单播模式:true 时每个事件只执行排序最前的一个匹配订阅(确定性顺序:类型全名::方法名::事件ID);
        /// false(默认)广播到全部匹配订阅。</summary>
        public bool Unicast { get; set; }

        /// <summary>停机排干超时(秒):停机后限时消费剩余事件;超时后未处理事件计数上报日志与指标。默认 5。</summary>
        public int DrainTimeoutSeconds { get; set; } = 5;
    }

    /// <summary>事件订阅者标记接口:实现类中被 [EventSubscribe] 标注的方法成为处理器。</summary>
    public interface IEventSubscriber
    {
    }

    /// <summary>
    /// 事件订阅特性:可叠加多个;EventId 支持字符串或枚举(ToString 约定);
    /// FuzzyMatch=true 时 EventId 按正则解释;NumRetries/RetryTimeoutMs/ExceptionTypes 定义该处理器的细粒度重试。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class EventSubscribeAttribute : Attribute
    {
        public EventSubscribeAttribute(string eventId)
        {
            EventId = eventId;
        }

        /// <summary>枚举事件 ID(特性参数以 object 装箱,按 ToString 约定与字符串事件匹配)。</summary>
        public EventSubscribeAttribute(object eventId)
        {
            EventId = eventId.ToString() ?? string.Empty;
        }

        public string EventId { get; }

        /// <summary>true 时 EventId 按正则解释,匹配任意满足模式的事件。</summary>
        public bool FuzzyMatch { get; set; }

        /// <summary>失败重试次数(0=不重试)。</summary>
        public int NumRetries { get; set; }

        /// <summary>重试间隔(毫秒)。</summary>
        public int RetryTimeoutMs { get; set; }

        /// <summary>仅对这些异常类型重试;空=所有异常。</summary>
        public Type[]? ExceptionTypes { get; set; }
    }

    /// <summary>事件处理上下文:事件载荷 + 发布时刻的框架上下文快照(身份/租户/链路)。</summary>
    public sealed class EventContext
    {
        public EventContext(string eventId, object? payload, NexMeshContext? context, DateTimeOffset createdAt, CancellationToken cancellationToken)
        {
            EventId = eventId;
            Payload = payload;
            Context = context;
            CreatedAt = createdAt;
            CancellationToken = cancellationToken;
        }

        public string EventId { get; }

        public object? Payload { get; }

        /// <summary>发布时刻的框架上下文快照(经 INexMeshContextAccessor 捕获);处理器内环境上下文已被再水化为该快照。</summary>
        public NexMeshContext? Context { get; }

        public DateTimeOffset CreatedAt { get; }

        public CancellationToken CancellationToken { get; }

        /// <summary>按目标类型取载荷;载荷为 null 或类型不符返回 default。</summary>
        public T? GetPayload<T>()
        {
            return Payload is T typed ? typed : default;
        }
    }

    /// <summary>事件发布器:入队即返回(队列满时回压等待);处理异步进行。</summary>
    public interface IEventPublisher
    {
        /// <summary>发布事件(字符串事件 ID)。</summary>
        Task PublishAsync(string eventId, object? payload = null, CancellationToken cancellationToken = default);

        /// <summary>发布事件(枚举事件 ID,按 ToString 约定与订阅匹配)。</summary>
        Task PublishAsync(Enum eventId, object? payload = null, CancellationToken cancellationToken = default);
    }

    /// <summary>事件监视器:每个处理器执行前后回调(日志/审计扩展点;异常不影响处理)。</summary>
    public interface IEventMonitor
    {
        Task OnExecutingAsync(EventContext context, string handlerName, CancellationToken cancellationToken);

        Task OnExecutedAsync(EventContext context, string handlerName, Exception? exception, CancellationToken cancellationToken);
    }

    /// <summary>重试耗尽后的故障处理扩展点(默认仅记日志;业务可实现持久化死信等)。</summary>
    public interface IEventFaultHandler
    {
        Task HandleAsync(EventContext context, string handlerName, Exception exception, CancellationToken cancellationToken);
    }
