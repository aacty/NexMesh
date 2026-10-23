namespace NexMesh.Core.Context;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// 离线任务执行纪律的唯一入口:五步收口,业务不可能绕过断链陷阱——
/// ① Activity 以快照 TraceContext 接续(断链 2);② Enter 显式绑定 AsyncLocal(断链 1);
/// ③ 每任务独立 DI 作用域,禁止闭包捕获 Scoped 服务(断链 4);
/// ④ 取消令牌 = 调用方令牌 + 宿主停机令牌,与任何 HTTP 请求无关(断链 3);
/// ⑤ 作用域/环境槽/Activity 随任务结束全部还原。
/// </summary>
public interface INexMeshBackgroundRunner
{
    /// <summary>在快照上下文中执行离线任务;work 的服务参数来自全新作用域,令牌与请求生命周期无关。</summary>
    Task RunAsync(
        NexMeshContext? context,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

public sealed class NexMeshBackgroundRunner(
    IServiceProvider rootProvider,
    IHostApplicationLifetime lifetime,
    INexMeshContextAccessor accessor) : INexMeshBackgroundRunner
{
    private static readonly ActivitySource Source = new("NexMesh.Background");

    public async Task RunAsync(
        NexMeshContext? context,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);

        using var activity = Source.StartActivity(
            "nexmesh.background",
            ActivityKind.Consumer,   // 队列消费者语义:后台任务以 Consumer Span 接续原链路
            parentContext: context?.TraceContext ?? default);
        if (activity is not null)
        {
            activity.SetTag("nexmesh.context.user_id", context?.UserId);
            activity.SetTag("nexmesh.context.tenant_id", context?.TenantId);
        }

        using var ambient = context is null ? null : accessor.Enter(context);
        // SSS+第二轮修复:租户槽随作用域同步/还原——审批执行、工作流步骤等后台路径
        // 的租户全局过滤(S4)与语义缓存隔离(S5)才能读到快照租户而非触发请求的租户
        using var tenantScope = TenantHelper.Enter(context?.TenantId);
        using var scope = rootProvider.CreateScope();

        await work(scope.ServiceProvider, linked.Token);
    }
}
