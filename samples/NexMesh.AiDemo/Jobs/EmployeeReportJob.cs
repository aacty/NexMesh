namespace NexMesh.AiDemo.Jobs;

using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexMesh.AiDemo.Entities;
using NexMesh.Worker;

/// <summary>
/// 定时作业示例:每分钟统计在职人数写日志。业务只写 RunOnceAsync——
/// Cron 调度/异常隔离/退避/停机/系统身份快照全部由基类托管(ICurrentUser 可读 system:EmployeeReportJob)。
/// </summary>
public class EmployeeReportJob(
    string cron, bool runOnStart,IServiceProvider sp) : NexMeshCronJob(cron, runOnStart, provider: sp,logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
{
    private readonly ILogger _logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("EmployeeReportJob");
    protected override async Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<IFreeSql>();
        var count = await db.Select<Employee>().CountAsync(ct);
        _logger.LogInformation("[定时作业] 当前在职员工 {Count} 人", count);
    }
}
