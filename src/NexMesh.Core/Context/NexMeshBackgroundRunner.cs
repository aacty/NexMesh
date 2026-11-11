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

    /// <summary>停机排干专用分发:五步纪律同 RunAsync,但令牌**不与宿主停机令牌联动**——
    /// 排干阶段宿主 ApplicationStopping 已触发,联动会使联合令牌创建即取消(SSS+交叉审核实证),
    /// 支持取消的处理器永远拿不到"新鲜令牌"。调用方自带限时令牌(排干超时语义由调用方负责)。
    /// 默认实现委托 RunAsync(与既有行为一致,自定义实现者零破坏)。</summary>
    Task RunDrainAsync(
        NexMeshContext? context,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken)
        => RunAsync(context, work, cancellationToken);
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

        // SSS+第二轮修复(双槽单作用域):租户槽随作用域同步/还原——审批执行、工作流步骤等后台路径
        // 的租户全局过滤(S4)与语义缓存隔离(S5)才能读到快照租户而非触发请求的租户;
        // 无快照时身份槽不动、仅显式清租户(防后台分支继承触发请求的租户)
        using var ambient = ContextSlotBinding.Enter(accessor, context);
        using var scope = rootProvider.CreateScope();

        await work(scope.ServiceProvider, linked.Token);
    }

    /// <summary>排干分发:同五步纪律,唯一差异=令牌只用调用方传入的(不联动 ApplicationStopping)。</summary>
    public async Task RunDrainAsync(
        NexMeshContext? context,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity(
            "nexmesh.background.drain",
            ActivityKind.Consumer,
            parentContext: context?.TraceContext ?? default);
        if (activity is not null)
        {
            activity.SetTag("nexmesh.context.user_id", context?.UserId);
            activity.SetTag("nexmesh.context.tenant_id", context?.TenantId);
        }

        using var ambient = ContextSlotBinding.Enter(accessor, context);
        using var scope = rootProvider.CreateScope();

        await work(scope.ServiceProvider, cancellationToken);
    }
}
