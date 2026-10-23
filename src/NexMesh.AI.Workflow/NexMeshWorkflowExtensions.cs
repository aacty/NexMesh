using FreeSql;
namespace NexMesh.AI.Workflow;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexMesh.Core.Context;

/// <summary>Workflow 注册扩展。</summary>
public static class NexMeshWorkflowExtensions
{
    /// <summary>注册工作流引擎(检查点存储:Memory 默认 | FreeSql 生产)。</summary>
    public static IServiceCollection AddNexMeshWorkflow(
        this IServiceCollection services, IConfiguration configuration)
    {
        var storeType = configuration["NexMesh:AI:Workflow:Store"] ?? "Memory";
        services.TryAddSingleton<IWorkflowCheckpointStore>(sp =>
            storeType.Equals("FreeSql", StringComparison.OrdinalIgnoreCase)
                ? new FreeSqlCheckpointStore(sp.GetRequiredService<IFreeSql>())
                : new InMemoryCheckpointStore());
        services.AddSingleton<NexMeshWorkflowEngine>();
        return services;
    }
}

/// <summary>FreeSql 检查点存储(生产:表 nexmesh_workflow_checkpoints,可审计可恢复)。</summary>
public sealed class FreeSqlCheckpointStore(IFreeSql db) : IWorkflowCheckpointStore
{
    private readonly AsyncLazy _tableSync = new(() =>
        Task.Run(() => db.CodeFirst.SyncStructure<CheckpointEntity>()));

    public async Task SaveAsync(WorkflowCheckpoint checkpoint)
    {
        await _tableSync.Value;
        await db.InsertOrUpdate<CheckpointEntity>().SetSource(ToEntity(checkpoint)).ExecuteAffrowsAsync();
    }

    public async Task<WorkflowCheckpoint?> GetAsync(string workflowId)
    {
        await _tableSync.Value;
        var e = await db.Select<CheckpointEntity>().Where(c => c.WorkflowId == workflowId).FirstAsync();
        return e is null ? null : ToRecord(e);
    }

    public async Task<List<WorkflowCheckpoint>> ListSuspendedAsync()
    {
        await _tableSync.Value;
        var entities = await db.Select<CheckpointEntity>()
            .Where(c => c.OverallStatus == StepStatus.Suspended)
            .ToListAsync();
        return entities.Select(ToRecord).ToList();
    }

    /// <summary>CAS:Suspended→Running 条件更新,影响行数=0 即并发冲突(他人已恢复)。</summary>
    public async Task<bool> TryMarkRunningAsync(string workflowId, DateTime resumedAt)
    {
        await _tableSync.Value;
        var affected = await db.Update<CheckpointEntity>()
            .Set(c => c.OverallStatus, StepStatus.Running)
            .Set(c => c.ResumedAt, resumedAt)
            .Where(c => c.WorkflowId == workflowId && c.OverallStatus == StepStatus.Suspended)
            .ExecuteAffrowsAsync();
        return affected > 0;
    }

    [FreeSql.DataAnnotations.Table(Name = "nexmesh_workflow_checkpoints")]
    public class CheckpointEntity
    {
        [FreeSql.DataAnnotations.Column(IsPrimary = true, StringLength = 64)]
        public string WorkflowId { get; set; } = "";
        [FreeSql.DataAnnotations.Column(StringLength = 256)]
        public string WorkflowName { get; set; } = "";
        public int CurrentStepIndex { get; set; }
        public StepStatus OverallStatus { get; set; }
        [FreeSql.DataAnnotations.Column(StringLength = -1)]
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResumedAt { get; set; }
        [FreeSql.DataAnnotations.Column(StringLength = -1)]
        public string ContextHeadersJson { get; set; } = "{}";
        [FreeSql.DataAnnotations.Column(StringLength = -1)]
        public string DataJson { get; set; } = "{}";
    }

    private static CheckpointEntity ToEntity(WorkflowCheckpoint c) => new()
    {
        WorkflowId = c.WorkflowId,
        WorkflowName = c.WorkflowName,
        CurrentStepIndex = c.CurrentStepIndex,
        OverallStatus = c.OverallStatus,
        LastError = c.LastError,
        CreatedAt = c.CreatedAt,
        ResumedAt = c.ResumedAt,
        ContextHeadersJson = System.Text.Json.JsonSerializer.Serialize(c.ContextHeaders),
        DataJson = c.DataJson,
    };

    private static WorkflowCheckpoint ToRecord(CheckpointEntity e) => new()
    {
        WorkflowId = e.WorkflowId,
        WorkflowName = e.WorkflowName,
        CurrentStepIndex = e.CurrentStepIndex,
        OverallStatus = e.OverallStatus,
        LastError = e.LastError,
        CreatedAt = e.CreatedAt,
        ResumedAt = e.ResumedAt,
        ContextHeaders = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(e.ContextHeadersJson) ?? [],
        DataJson = e.DataJson,
    };

    private sealed class AsyncLazy(Func<Task> factory)
    {
        private readonly Task _task = factory();
        public Task Value => _task;
    }
}
