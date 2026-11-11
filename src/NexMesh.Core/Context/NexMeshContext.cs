namespace NexMesh.Core.Context;

using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// 跨异步边界的唯一货币:不可变的身份/租户/链路快照(HTTP 请求上下文的可序列化投影)。
/// 后台任务(Cron/队列唤醒/Checkpoint 恢复)没有 HttpContext——触发时把本对象序列化进
/// 工作流状态或消息头,执行时由框架显式绑定(Enter)还原到 AsyncLocal,身份链路永不丢失。
/// </summary>
public sealed record NexMeshContext
{
    public string? UserId { get; init; }
    public string? UserName { get; init; }

    /// <summary>租户/院区统一槽位(HospitalCode、TenantCode 等租户标识均落到这里)。</summary>
    public string? TenantId { get; init; }

    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    /// <summary>链路上下文(W3C 可传播),后台开新 Span 时以 Link/Parent 接续。</summary>
    public ActivityContext TraceContext { get; init; }

    /// <summary>序列化为消息头(队列/工作流状态/审批记录的持久化形态)。</summary>
    public IReadOnlyDictionary<string, string> ToMessageHeaders()
    {
        var headers = new Dictionary<string, string>();
        if (UserId is not null) headers["nm-user-id"] = UserId;
        if (UserName is not null) headers["nm-user-name"] = UserName;
        if (TenantId is not null) headers["nm-tenant-id"] = TenantId;
        if (Claims.Count > 0) headers["nm-claims"] = JsonSerializer.Serialize(Claims);
        if (TraceContext != default)
        {
            headers["traceparent"] =
                $"00-{TraceContext.TraceId}-{TraceContext.SpanId}-{(byte)TraceContext.TraceFlags:00}";
        }
        return headers;
    }

    /// <summary>从消息头还原快照(无 NexMesh 头时返回 null)。</summary>
    public static NexMeshContext? FromMessageHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("nm-user-id", out var userId)
            && !headers.TryGetValue("nm-tenant-id", out _))
        {
            return null;
        }

        var claims = headers.TryGetValue("nm-claims", out var claimsJson)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(claimsJson) ?? []
            : [];
        var traceContext = headers.TryGetValue("traceparent", out var traceparent)
            && ActivityContext.TryParse(traceparent, null, out var parsed)
                ? parsed
                : default;

        return new NexMeshContext
        {
            UserId = userId,
            UserName = headers.GetValueOrDefault("nm-user-name"),
            TenantId = headers.GetValueOrDefault("nm-tenant-id"),
            Claims = claims,
            TraceContext = traceContext,
        };
    }
}

/// <summary>
/// 环境槽位唯一持有者:身份/租户两个 AsyncLocal 同处一处——单独绑定(TenantHelper/Accessor)与
/// 双槽绑定(ContextSlotBinding)共用同一槽位实例,AsyncLocal 身份不因重构漂移。
/// </summary>
internal static class ContextSlots
{
    internal static readonly AsyncLocal<NexMeshContext?> Context = new();
    internal static readonly AsyncLocal<string?> Tenant = new();
}

/// <summary>
/// 租户运行时取值器(AsyncLocal,跨包共享):Data.FreeSql 的全局过滤、SemanticCache 的缓存键隔离均使用。
/// 由 NexMeshContextMiddleware(请求入口)与 Repository 构造(仓储路径)写入。
/// </summary>
public static class TenantHelper
{
    /// <summary>
    /// 当前租户 ID。null 语义(SSS+第二轮修正,与全局过滤对齐,fail-closed):
    /// 过滤条件为 `TenantId == Current`——null 时匹配"未指定租户"的行,而非"不过滤"。
    /// 系统路径(后台任务)的租户由 RunAsync/中间件显式设置,不依赖默认值。
    /// </summary>
    public static string? Current => ContextSlots.Tenant.Value;

    /// <summary>设置当前租户(遗留入口,建议用 Enter 作用域)。</summary>
    public static void Set(string? tenantId) => ContextSlots.Tenant.Value = tenantId;

    /// <summary>
    /// 作用域式绑定:写入租户槽,Dispose 还原先前值(嵌套安全)。
    /// 中间件/BackgroundRunner 统一使用——修复"只设不清"的槽泄漏。
    /// </summary>
    public static IDisposable Enter(string? tenantId)
    {
        var previous = ContextSlots.Tenant.Value;
        ContextSlots.Tenant.Value = tenantId;
        return new RestoreScope(previous, tenantId);
    }

    /// <summary>
    /// 租户槽还原作用域(SSS+ 二轮修复,原 A4 实证缺口):
    /// ① 幂等——双重 Dispose 第二次为 no-op(否则把陈旧快照二次写回,踩掉其间的新绑定);
    /// ② 世代校验——Dispose 时若槽当前值 ≠ 本作用域设置的值(错序释放:活跃的后来者已改写槽),
    /// 只摘除自己不还原,绝不把陈旧值覆盖新值。
    /// </summary>
    private sealed class RestoreScope(string? previous, string? setValue) : IDisposable
    {
        private int _disposed; // 0=活跃,1=已释放

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return; // 双重 Dispose 幂等
            }
            // 世代校验:槽值已被后来者改写(错序释放)时静默退场,不覆盖新值
            if (!string.Equals(ContextSlots.Tenant.Value, setValue, StringComparison.Ordinal))
            {
                return;
            }
            ContextSlots.Tenant.Value = previous;
        }
    }
}

/// <summary>
/// 环境槽(AsyncLocal)读写与显式绑定作用域:请求入口自动快照;离线任务恢复时
/// Enter(快照) 显式还原,Dispose 自动还原先前值——嵌套绑定天然安全。
/// </summary>
public interface INexMeshContextAccessor
{
    NexMeshContext? Current { get; }

    /// <summary>显式绑定:将快照写回 AsyncLocal,返回的作用域 Dispose 时还原先前值。</summary>
    IDisposable Enter(NexMeshContext context);
}

public sealed class AsyncLocalNexMeshContextAccessor : INexMeshContextAccessor
{
    public NexMeshContext? Current => ContextSlots.Context.Value;

    public IDisposable Enter(NexMeshContext context)
    {
        var previous = ContextSlots.Context.Value;
        ContextSlots.Context.Value = context;
        return new RestoreScope(previous, context);
    }

    /// <summary>双槽绑定(身份+租户):单作用域对象,Dispose 按嵌套逆序(租户→身份)还原先前值。
    /// 供框架请求入口/后台任务路径使用,较两次独立 Enter 少 1 个作用域对象与 1 次接口分发。</summary>
    internal IDisposable EnterWithTenant(NexMeshContext context)
    {
        var previousContext = ContextSlots.Context.Value;
        var previousTenant = ContextSlots.Tenant.Value;
        ContextSlots.Context.Value = context;
        ContextSlots.Tenant.Value = context.TenantId;
        return new DualRestoreScope(previousContext, previousTenant, context);
    }

    /// <summary>身份槽还原作用域(SSS+ 二轮修复):幂等(双重 Dispose no-op)+ 世代校验(错序释放不覆盖新值)。</summary>
    private sealed class RestoreScope(NexMeshContext? previous, NexMeshContext setValue) : IDisposable
    {
        private int _disposed; // 0=活跃,1=已释放

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return; // 双重 Dispose 幂等
            }
            // 世代校验:槽当前值是另一个绑定实例(等值不同引用也算新世代)时只摘除自己
            if (!ReferenceEquals(ContextSlots.Context.Value, setValue))
            {
                return;
            }
            ContextSlots.Context.Value = previous;
        }
    }

    /// <summary>双槽还原作用域(SSS+ 二轮修复):幂等 + 逐槽世代校验——任一槽被后来者改写时,该槽只摘除自己不覆盖新值。</summary>
    private sealed class DualRestoreScope(NexMeshContext? previousContext, string? previousTenant, NexMeshContext setValue) : IDisposable
    {
        private int _disposed; // 0=活跃,1=已释放

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return; // 双重 Dispose 幂等
            }
            if (string.Equals(ContextSlots.Tenant.Value, setValue.TenantId, StringComparison.Ordinal))
            {
                ContextSlots.Tenant.Value = previousTenant;
            }
            if (ReferenceEquals(ContextSlots.Context.Value, setValue))
            {
                ContextSlots.Context.Value = previousContext;
            }
        }
    }
}

/// <summary>
/// 框架路径的双槽绑定收口(请求入口中间件/BackgroundRunner 专用,不外露):
/// 默认 accessor 走单作用域双槽绑定;自定义 accessor 实现自动回退组合两次 Enter(行为一致);
/// 无快照(context=null)时身份槽保持不动(不伪造上下文),仅显式清租户——防后台分支继承触发请求的租户。
/// </summary>
internal static class ContextSlotBinding
{
    internal static IDisposable? Enter(INexMeshContextAccessor accessor, NexMeshContext? context)
    {
        if (context is null)
        {
            return TenantHelper.Enter(null);
        }
        if (accessor is AsyncLocalNexMeshContextAccessor local)
        {
            return local.EnterWithTenant(context);
        }
        var contextScope = accessor.Enter(context);
        var tenantScope = TenantHelper.Enter(context.TenantId);
        return new CompositeScope(tenantScope, contextScope);
    }

    /// <summary>组合作用域(SSS+ 二轮修复):幂等守卫;内层作用域自带世代校验,错序/重复释放不会覆盖新值。</summary>
    private sealed class CompositeScope(IDisposable tenantScope, IDisposable contextScope) : IDisposable
    {
        private int _disposed; // 0=活跃,1=已释放

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return; // 双重 Dispose 幂等(内层作用域也被各自的世代守卫保护)
            }
            tenantScope.Dispose(); // 嵌套逆序:租户先还原(与历史 using 嵌套一致)
            contextScope.Dispose();
        }
    }
}
