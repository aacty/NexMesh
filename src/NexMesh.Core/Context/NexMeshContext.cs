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
/// 租户运行时取值器(AsyncLocal,跨包共享):Data.FreeSql 的全局过滤、SemanticCache 的缓存键隔离均使用。
/// 由 NexMeshContextMiddleware(请求入口)与 Repository 构造(仓储路径)写入。
/// </summary>
public static class TenantHelper
{
    private static readonly AsyncLocal<string?> Slot = new();

    /// <summary>
    /// 当前租户 ID。null 语义(SSS+第二轮修正,与全局过滤对齐,fail-closed):
    /// 过滤条件为 `TenantId == Current`——null 时匹配"未指定租户"的行,而非"不过滤"。
    /// 系统路径(后台任务)的租户由 RunAsync/中间件显式设置,不依赖默认值。
    /// </summary>
    public static string? Current => Slot.Value;

    /// <summary>设置当前租户(遗留入口,建议用 Enter 作用域)。</summary>
    public static void Set(string? tenantId) => Slot.Value = tenantId;

    /// <summary>
    /// 作用域式绑定:写入租户槽,Dispose 还原先前值(嵌套安全)。
    /// 中间件/BackgroundRunner 统一使用——修复"只设不清"的槽泄漏。
    /// </summary>
    public static IDisposable Enter(string? tenantId)
    {
        var previous = Slot.Value;
        Slot.Value = tenantId;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(string? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
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
    private static readonly AsyncLocal<NexMeshContext?> Slot = new();

    public NexMeshContext? Current => Slot.Value;

    public IDisposable Enter(NexMeshContext context)
    {
        var previous = Slot.Value;
        Slot.Value = context;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(NexMeshContext? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
