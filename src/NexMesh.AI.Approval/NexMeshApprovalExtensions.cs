namespace NexMesh.AI.Approval;

using FreeSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.Models;

/// <summary>NexMesh 审批门注册与审批中心端点。</summary>
public static class NexMeshApprovalExtensions
{
    /// <summary>
    /// 注册审批门:配置节 NexMesh:AI:Approval(Enabled 开关由 NexMesh.AI 注册器读取;
    /// Store=Memory|FreeSql,FreeSql 需容器中已有 IFreeSql;DecidedByClaim 为审批人声明名,缺省 operator)。
    /// 内含幂等入队网关与决策服务;须与 AddNexMeshAi 配合使用。
    /// </summary>
    public static IServiceCollection AddNexMeshApproval(
        this IServiceCollection services, IConfiguration configuration)
    {
        var storeType = configuration["NexMesh:AI:Approval:Store"] ?? "Memory";

        services.AddSingleton<IApprovalStore>(sp =>
            storeType.Equals("FreeSql", StringComparison.OrdinalIgnoreCase)
                ? new FreeSqlApprovalStore(sp.GetRequiredService<IFreeSql>())
                : new InMemoryApprovalStore());
        services.AddSingleton<Tools.IToolApprovalGate, ApprovalGate>();
        services.AddSingleton<ApprovalDecisionService>();
        return services;
    }

    /// <summary>
    /// 审批中心端点:待审列表 / 详情 / 批准 / 拒绝。
    /// requireAuthorization=true 时全部端点要求已认证(生产建议开启;配合 UseNexMeshAuthentication)。
    /// </summary>
    public static IEndpointRouteBuilder MapNexMeshApprovals(
        this IEndpointRouteBuilder endpoints, bool requireAuthorization = true)
    {
        var pending = endpoints.MapGet("/api/approvals/pending", async (IApprovalStore store) =>
            Results.Ok(ApiResult<List<ApprovalRequest>>.Ok(await store.ListPendingAsync())));

        var detail = endpoints.MapGet("/api/approvals/{id}", async (string id, IApprovalStore store) =>
        {
            var request = await store.GetAsync(id);
            return request is null
                ? Results.NotFound(ApiResult<object>.Fail(40401, $"审批单 {id} 不存在"))
                : Results.Ok(ApiResult<ApprovalRequest>.Ok(request));
        });

        var approve = endpoints.MapPost("/api/approvals/{id}/approve", async (
            string id, ApprovalDecisionService decisions, NexMesh.Core.ICurrentUser user) =>
            Results.Ok(ApiResult<ApprovalRequest>.Ok(
                await decisions.ApproveAsync(id, user.UserId ?? "operator")))); // SSS+:取认证身份,不可伪造

        var reject = endpoints.MapPost("/api/approvals/{id}/reject", async (
            string id, ApprovalDecisionService decisions, NexMesh.Core.ICurrentUser user) =>
            Results.Ok(ApiResult<ApprovalRequest>.Ok(
                await decisions.RejectAsync(id, user.UserId ?? "operator"))));

        if (requireAuthorization)
        {
            pending.RequireAuthorization();
            detail.RequireAuthorization();
            approve.RequireAuthorization();
            reject.RequireAuthorization();
        }
        return endpoints;
    }
}

/// <summary>审批决策请求体(操作人,缺省 operator;生产由 Auth 注入真实身份)。</summary>
public record ApprovalDecisionRequest(string? DecidedBy);
