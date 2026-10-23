namespace NexMesh.AiDemo.Tools;

using FreeSql;
using Microsoft.Extensions.AI;
using NexMesh.AiDemo.Entities;
using NexMesh.AI.Core;
using NexMesh.AI.Tools;
using NexMesh.Core.DependencyInjection;

/// <summary>
/// 业务与 AI 融合的一等公民样板:同一个实例方法内部既走 FreeSql 业务查询、又调用 LLM——
/// IFreeSql 与 INexMeshChatFactory 由 DI 注入,每次工具调用在独立作用域解析,生命周期框架托管。
/// </summary>
public class OrderDiagnoseService(IFreeSql db, INexMeshChatFactory chat) : IScopedService
{
    [NexMeshTool("根据员工号诊断在职状态并给出管理建议(业务与AI融合演示)。", NexMeshRiskLevel.Low)]
    public async Task<string> DiagnoseAsync(long employeeId)
    {
        var employee = await db.Select<Employee>()
            .Where(e => e.Id == employeeId && !e.IsDeleted)
            .FirstAsync();
        if (employee is null)
        {
            return $"未找到员工 {employeeId}";
        }

        var response = await chat.CreateClient().GetResponseAsync(
        [
            new ChatMessage(ChatRole.User,
                $"员工档案:{{\"name\":\"{employee.Name}\",\"department\":\"{employee.Department}\"}}。请用一句话给出管理建议。"),
        ]);
        return $"【{employee.Name}/{employee.Department}】{response.Text}";
    }
}
