namespace NexMesh.AiFixtures.High;

using NexMesh.AI.Tools;

/// <summary>High 风险工具夹具(独立程序集):发现即应触发安全门启动阻断。</summary>
public static class HighRiskFixture
{
    [NexMeshTool("对指定订单执行管理员强行退款。高危写操作!", NexMeshRiskLevel.High)]
    public static Task<string> ForceRefundAsync(string orderId, string reason)
        => Task.FromResult($"订单 {orderId} 已退款:{reason}");
}
