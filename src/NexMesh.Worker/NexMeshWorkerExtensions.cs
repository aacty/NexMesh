namespace NexMesh.Worker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>定时作业注册。</summary>
public static class NexMeshWorkerExtensions
{
    /// <summary>
    /// 注册 Cron 作业:TJob 构造为 (string cron, bool runOnStart, IServiceProvider sp)。
    /// 例:builder.Services.AddNexMeshCronJob&lt;ReportJob&gt;("0 2 * * *")——每日凌晨 2 点。
    /// </summary>
    public static IServiceCollection AddNexMeshCronJob<TJob>(
        this IServiceCollection services, string cron, bool runOnStart = false)
        where TJob : NexMeshCronJob
    {
        services.AddSingleton(sp =>
            (TJob)ActivatorUtilities.CreateInstance(sp, typeof(TJob), cron, runOnStart, sp));
        services.AddHostedService(sp => sp.GetRequiredService<TJob>());
        return services;
    }

    /// <summary>注册周期作业(TimeSpan):TJob 构造为 (TimeSpan, IServiceProvider)。</summary>
    public static IServiceCollection AddNexMeshIntervalJob<TJob>(this IServiceCollection services)
        where TJob : NexMeshIntervalJob
    {
        services.AddSingleton<TJob>();
        services.AddHostedService(sp => sp.GetRequiredService<TJob>());
        return services;
    }
}
