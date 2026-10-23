namespace NexMesh.AI.Approval;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Tools;
using NexMesh.Core.Context;
using NexMesh.Core;

/// <summary>
/// 审批决策与执行:批准 → 经 BackgroundRunner 在审批单快照的再水化上下文里、
/// 独立 DI 作用域中执行原始工具(五步纪律:Consumer Span 接续/Enter 绑定/新作用域/双令牌/全还原);
/// 拒绝 → 仅落状态。执行失败记录 Error,不回喂任何模型。
/// </summary>
public sealed class ApprovalDecisionService(
    IApprovalStore store,
    INexMeshToolRegistry toolRegistry,
    INexMeshBackgroundRunner runner,
    ILogger<ApprovalDecisionService>? logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>批准并执行。返回执行后的审批单;状态机非法流转抛 MeshException。</summary>
    public async Task<ApprovalRequest> ApproveAsync(string id, string decidedBy)
    {
        var request = await GetPendingOrThrow(id);
        request = request with { Status = ApprovalStatus.Approved, DecidedAt = DateTime.Now, DecidedBy = decidedBy };
        await store.UpdateAsync(request);

        var snapshot = NexMeshContext.FromMessageHeaders(request.ContextHeaders);
        try
        {
            string? result = null;
            await runner.RunAsync(snapshot, async (sp, ct) =>
            {
                var raw = ResolveRawFunction(request.ToolName)
                    ?? throw new MeshException($"审批执行失败:工具 {request.ToolName} 已不可用", 50002, 500);
                var arguments = BuildArguments(request.ArgumentsJson);
                var value = await raw.InvokeAsync(arguments, ct);
                result = value?.ToString();
            });

            request = request with { Status = ApprovalStatus.Executed, Result = result };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "审批单 {ApprovalId} 批准执行失败(工具 {ToolName})", request.Id, request.ToolName);
            request = request with { Status = ApprovalStatus.Failed, Error = ex.Message };
        }
        await store.UpdateAsync(request);
        return request;
    }

    /// <summary>拒绝:仅落状态,永不执行。</summary>
    public async Task<ApprovalRequest> RejectAsync(string id, string decidedBy)
    {
        var request = await GetPendingOrThrow(id);
        request = request with
        {
            Status = ApprovalStatus.Rejected,
            DecidedAt = DateTime.Now,
            DecidedBy = decidedBy,
        };
        await store.UpdateAsync(request);
        return request;
    }

    /// <summary>查找原始工具:注册器暴露的未包装函数(挂起包装会造成无限再入队)。</summary>
    private AIFunction? ResolveRawFunction(string toolName) => toolRegistry.FindRawFunction(toolName);

    private static AIFunctionArguments BuildArguments(string argumentsJson)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson, Json) ?? [];
        return new AIFunctionArguments(
            dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<ApprovalRequest> GetPendingOrThrow(string id)
    {
        var request = await store.GetAsync(id)
            ?? throw new MeshException($"审批单 {id} 不存在", 40401, 404);
        if (request.Status != ApprovalStatus.Pending)
        {
            throw new MeshException($"审批单 {id} 已处于 {request.Status} 状态,不可重复决策", 40903, 409);
        }
        return request;
    }
}
