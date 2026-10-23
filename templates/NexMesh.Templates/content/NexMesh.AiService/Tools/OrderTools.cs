using FreeSql;
using NexMesh.AI.Tools;
using NexMesh.AiService.Entities;
using NexMesh.Core.DependencyInjection;

namespace NexMesh.AiService.Tools;

/// <summary>
/// AI 工具:静态低风险 + 实例高危(审批门挂起拦截)。
/// 实例工具注入 IFreeSql,生命周期由框架托管(每次调用独立作用域)。
/// </summary>
public class OrderTools(IFreeSql db) : IScopedService
{
    [NexMeshTool("根据订单号查询订单状态。低风险只读。", NexMeshRiskLevel.Low)]
    public async Task<string> QueryAsync(long orderId)
    {
        var order = await db.Select<Order>().Where(o => o.Id == orderId).FirstAsync();
        return order is null ? $"订单 {orderId} 不存在" : $"订单 {orderId}:客户 {order.Customer},金额 {order.Amount:C}";
    }

    [NexMeshTool("对指定订单执行强制退款。高危写操作!", NexMeshRiskLevel.High)]
    public async Task<string> RefundAsync(long orderId, string reason)
    {
        var affected = await db.Update<Order>()
            .Set(o => o.Customer, o => o.Customer + " [已退款]")
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .ExecuteAffrowsAsync();
        return affected > 0 ? $"订单 {orderId} 已退款:{reason}" : $"订单 {orderId} 不存在或已退款";
    }
}

/// <summary>演示登录验证器(生产:查用户表+密码哈希)。</summary>
public class DemoLoginValidator : ILoginValidator, ITransientService
{
    public Task<LoginUser?> ValidateAsync(string username, string password) =>
        Task.FromResult<LoginUser?>(
            username == "admin" && password == "admin123"
                ? new LoginUser("admin-001", "管理员", "hosp-A",
                    new Dictionary<string, string> { ["role"] = "admin" })
                : null);
}
