namespace NexMesh.Data;

/// <summary>
/// 租户全局过滤取值器:委托到 Core 的 TenantHelper(单一 AsyncLocal 槽,跨包共享)。
/// FreeSql GlobalFilter 的 lambda 在查询执行时惰性读取。
/// </summary>
public static class TenantAccessor
{
    /// <summary>当前租户 ID。fail-closed 语义:null 时全局过滤匹配"未指定租户"的行(见 Core.TenantHelper 文档)。</summary>
    public static string? Current => NexMesh.Core.Context.TenantHelper.Current;
}
