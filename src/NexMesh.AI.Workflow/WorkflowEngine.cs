namespace NexMesh.AI.Workflow;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexMesh.Core;
using NexMesh.Core.Context;

/// <summary>工作流步骤状态。</summary>
public enum StepStatus
{
    Pending = 0,
    Running = 1,
    Suspended = 2,   // HITL 挂起(等待审批/人工输入)
    Completed = 3,
    Failed = 4,
    Skipped = 5,
}

/// <summary>工作流步骤上下文(传给业务步骤的执行环境)。</summary>
public sealed class StepContext(IServiceProvider scoped)
{
    public IServiceProvider Services { get; } = scoped;
    public Dictionary<string, object?> Outputs { get; } = [];
    public Dictionary<string, object?> Inputs { get; } = [];
    public CancellationToken CancellationToken { get; set; }

    /// <summary>本次是否为挂起后恢复执行(步骤可据此跳过已落库的副作用)。</summary>
    public bool IsResumed { get; internal set; }

    /// <summary>
    /// 类型安全读取输入:挂起/崩溃恢复后 Inputs 值经 JSON 往返塌缩为 JsonElement,
    /// 直接强转会 InvalidCastException——一律经此方法读取(JsonElement 自动反序列化)。
    /// </summary>
    public T? GetInput<T>(string key)
    {
        if (!Inputs.TryGetValue(key, out var value) || value is null)
        {
            return default;
        }
        if (value is T typed)
        {
            return typed;
        }
        if (value is System.Text.Json.JsonElement element)
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText());
        }
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new MeshException(
                $"工作流输入 '{key}' 类型不匹配:期望 {typeof(T).Name},实际 {value.GetType().Name}", 50003, 500);
        }
    }
}

/// <summary>
/// 工作流步骤基类:业务继承并实现 ExecuteAsync。
/// 返回 StepResult.Completed(继续下一步) / Suspended(挂起等待人工) / Skipped(跳过)。
/// </summary>
public abstract class NexMeshWorkflowStep
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    public abstract Task<StepStatus> ExecuteAsync(StepContext context);
}

/// <summary>工作流检查点(持久化形态:步骤执行状态 + 步骤间数据 + 上下文快照)。</summary>
public sealed record WorkflowCheckpoint
{
    public string WorkflowId { get; init; } = Guid.CreateVersion7().ToString("N");
    public string WorkflowName { get; init; } = "";
    public int CurrentStepIndex { get; init; }
    public StepStatus OverallStatus { get; init; } = StepStatus.Pending;
    public string? LastError { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime? ResumedAt { get; init; }
    public IReadOnlyDictionary<string, string> ContextHeaders { get; init; } = new Dictionary<string, string>();
    /// <summary>步骤间共享数据(JSON 序列化)。</summary>
    public string DataJson { get; init; } = "{}";
    /// <summary>
    /// 步骤序列签名(步骤名有序拼接的哈希):恢复时校验代码步骤序列未变,
    /// 防止代码重发布后 CurrentStepIndex 指向不同步骤。
    /// </summary>
    public string StepsSignature { get; init; } = "";

    public static string ComputeStepsSignature(IEnumerable<NexMeshWorkflowStep> steps)
    {
        var names = string.Join("|", steps.Select(s => s.Name));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(names));
        return Convert.ToHexString(hash);
    }
}

/// <summary>检查点持久化。</summary>
public interface IWorkflowCheckpointStore
{
    Task SaveAsync(WorkflowCheckpoint checkpoint);
    Task<WorkflowCheckpoint?> GetAsync(string workflowId);
    Task<List<WorkflowCheckpoint>> ListSuspendedAsync();

    /// <summary>
    /// 原子状态转换 Suspended→Running(CAS):两个请求并发恢复同一工作流时只有一个成功,
    /// 杜绝 HITL 步骤重复执行。返回 false = 已被他人恢复/状态不符(调用方回 409)。
    /// </summary>
    Task<bool> TryMarkRunningAsync(string workflowId, DateTime resumedAt);
}

/// <summary>内存检查点存储(开发/测试)。</summary>
public sealed class InMemoryCheckpointStore : IWorkflowCheckpointStore
{
    private readonly Dictionary<string, WorkflowCheckpoint> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Task SaveAsync(WorkflowCheckpoint checkpoint)
    {
        lock (_lock) { _store[checkpoint.WorkflowId] = checkpoint; }
        return Task.CompletedTask;
    }

    public Task<WorkflowCheckpoint?> GetAsync(string workflowId)
    {
        lock (_lock) { return Task.FromResult(_store.GetValueOrDefault(workflowId)); }
    }

    public Task<List<WorkflowCheckpoint>> ListSuspendedAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_store.Values
                .Where(c => c.OverallStatus == StepStatus.Suspended)
                .ToList());
        }
    }

    public Task<bool> TryMarkRunningAsync(string workflowId, DateTime resumedAt)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(workflowId, out var current)
                || current.OverallStatus != StepStatus.Suspended)
            {
                return Task.FromResult(false);
            }
            _store[workflowId] = current with { OverallStatus = StepStatus.Running, ResumedAt = resumedAt };
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// 工作流引擎:按步骤序列执行 → 每步完成持久化检查点(崩溃可恢复) →
/// HITL 步骤挂起(Suspended)→ 恢复接口从断点继续。
/// 每步经 BackgroundRunner 在上下文快照的再水化环境中、独立作用域执行。
/// </summary>
public sealed class NexMeshWorkflowEngine(
    IWorkflowCheckpointStore store,
    INexMeshBackgroundRunner runner,
    ILogger<NexMeshWorkflowEngine>? logger = null)
{
    /// <summary>启动工作流(从头执行)。</summary>
    public async Task<WorkflowCheckpoint> StartAsync(
        string workflowName,
        List<NexMeshWorkflowStep> steps,
        NexMeshContext? context = null,
        Dictionary<string, object?>? initialData = null,
        CancellationToken ct = default)
    {
        if (steps.Count == 0)
        {
            throw new MeshException("工作流步骤列表为空,拒绝启动(防止静默假完成)", 40002, 400);
        }
        var checkpoint = new WorkflowCheckpoint
        {
            WorkflowName = workflowName,
            CurrentStepIndex = 0,
            OverallStatus = StepStatus.Running,
            ContextHeaders = context?.ToMessageHeaders() ?? new Dictionary<string, string>(),
            DataJson = SerializeSafe(initialData ?? new Dictionary<string, object?>(), workflowName, "初始数据"),
            StepsSignature = WorkflowCheckpoint.ComputeStepsSignature(steps),
        };
        await store.SaveAsync(checkpoint);
        return await ExecuteFromAsync(checkpoint, steps, ct);
    }

    /// <summary>从检查点恢复(断点续跑 / HITL 恢复)。CAS 防并发重复恢复;校验步骤签名未变。</summary>
    public async Task<WorkflowCheckpoint> ResumeAsync(string workflowId, List<NexMeshWorkflowStep> steps, CancellationToken ct = default)
    {
        var checkpoint = await store.GetAsync(workflowId)
            ?? throw new MeshException($"工作流 {workflowId} 不存在", 40401, 404);
        if (checkpoint.OverallStatus != StepStatus.Suspended)
        {
            throw new MeshException($"工作流 {workflowId} 状态为 {checkpoint.OverallStatus},仅 Suspended 可恢复", 40904, 409);
        }
        // 2h:步骤序列与启动时一致(代码重发布后索引错位在此拦截)
        var signature = WorkflowCheckpoint.ComputeStepsSignature(steps);
        if (!string.IsNullOrEmpty(checkpoint.StepsSignature) && checkpoint.StepsSignature != signature)
        {
            throw new MeshException($"工作流 {workflowId} 的步骤定义已变更,拒绝恢复", 40906, 409);
        }
        // SSS+第二轮:原子转换(CAS)——并发恢复只有一个赢家,输家 409
        if (!await store.TryMarkRunningAsync(workflowId, DateTime.Now))
        {
            throw new MeshException($"工作流 {workflowId} 已被恢复或状态已变更", 40905, 409);
        }
        checkpoint = checkpoint with { OverallStatus = StepStatus.Running, ResumedAt = DateTime.Now };
        return await ExecuteFromAsync(checkpoint, steps, ct, isResumed: true);
    }

    /// <summary>
    /// 2g:恢复卡死的 Running 检查点(进程崩溃于执行中途)→ 原子回退为 Suspended,
    /// 之后可走 ResumeAsync 正常续跑。仅 Running 状态可转换。
    /// </summary>
    public async Task<bool> RecoverStaleAsync(string workflowId)
    {
        var checkpoint = await store.GetAsync(workflowId);
        if (checkpoint is null || checkpoint.OverallStatus != StepStatus.Running)
        {
            return false;
        }
        await store.SaveAsync(checkpoint with { OverallStatus = StepStatus.Suspended });
        return true;
    }

    /// <summary>2f:序列化失败给出带上下文的 MeshException,而非裸 JsonException。</summary>
    private static string SerializeSafe(object value, string workflow, string location)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new MeshException(
                $"工作流 {workflow} 的{location}不可 JSON 序列化({ex.GetType().Name}):步骤输出必须为可序列化类型", 50005, 500);
        }
    }

    private async Task<WorkflowCheckpoint> ExecuteFromAsync(
        WorkflowCheckpoint checkpoint, List<NexMeshWorkflowStep> steps, CancellationToken ct, bool isResumed = false)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(checkpoint.DataJson) ?? [];
        var context = NexMeshContext.FromMessageHeaders(checkpoint.ContextHeaders);

        while (checkpoint.CurrentStepIndex < steps.Count)
        {
            var step = steps[checkpoint.CurrentStepIndex];
            StepStatus status;

            try
            {
                // 每步在 BackgroundRunner 的五步纪律中执行(上下文再水化 + 独立作用域)
                StepStatus captured = StepStatus.Failed;
                await runner.RunAsync(context, async (sp, token) =>
                {
                    var stepContext = new StepContext(sp)
                    {
                        CancellationToken = token,
                        IsResumed = isResumed, // 挂起步骤恢复后可据此跳过已落库副作用
                    };
                    foreach (var kv in data)
                    {
                        stepContext.Inputs[kv.Key] = kv.Value;
                    }
                    captured = await step.ExecuteAsync(stepContext);
                    foreach (var kv in stepContext.Outputs)
                    {
                        data[kv.Key] = kv.Value;
                    }
                }, ct);
                status = captured;
                isResumed = false; // 恢复轮只标记一次(当前挂起步骤);后续步骤为首次执行
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "工作流 {Workflow} 步骤 {Step} 失败", checkpoint.WorkflowName, step.Name);
                checkpoint = checkpoint with
                {
                    OverallStatus = StepStatus.Failed,
                    LastError = ex.Message,
                    DataJson = SerializeSafe(data, checkpoint.WorkflowName, $"步骤 {step.Name} 输出"),
                };
                await store.SaveAsync(checkpoint);
                return checkpoint;
            }

            if (status == StepStatus.Suspended)
            {
                // HITL 挂起:持久化当前进度,等待人工恢复
                checkpoint = checkpoint with
                {
                    OverallStatus = StepStatus.Suspended,
                    DataJson = SerializeSafe(data, checkpoint.WorkflowName, $"步骤 {step.Name} 输出"),
                };
                await store.SaveAsync(checkpoint);
                return checkpoint;
            }

            // Completed/Skipped → 推进到下一步
            checkpoint = checkpoint with
            {
                CurrentStepIndex = checkpoint.CurrentStepIndex + 1,
                DataJson = SerializeSafe(data, checkpoint.WorkflowName, $"步骤 {step.Name} 输出"),
            };
            await store.SaveAsync(checkpoint);
        }

        checkpoint = checkpoint with { OverallStatus = StepStatus.Completed };
        await store.SaveAsync(checkpoint);
        return checkpoint;
    }
}
