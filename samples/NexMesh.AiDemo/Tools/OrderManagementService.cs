namespace NexMesh.AiDemo.Tools;

using NexMesh.AI.Tools;

/// <summary>
/// AI 工具样例(Phase 1:仅静态方法)。
/// 注意:高危写操作(如强制退款)在 Phase 1 一旦标注 High 即启动阻断——该行为由
/// tests/NexMesh.AI.Tests 的 SecurityGateTests 验证,demo 内只保留低风险只读工具。
/// </summary>
public static class OrderManagementService
{
    [NexMeshTool("根据订单号查询运单状态。低风险只读。", NexMeshRiskLevel.Low)]
    public static Task<string> QueryOrderAsync(string orderId)
        => Task.FromResult($"订单 {orderId} 状态:已发货,物流单号 SF1009281。");

    [NexMeshTool("对指定订单执行管理员强制关闭。高危写操作!", NexMeshRiskLevel.High)]
    public static Task<string> ForceCloseOrderAsync(string orderId, string reason)
        => Task.FromResult($"订单 {orderId} 已强制关闭:{reason}");
}
