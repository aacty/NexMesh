namespace NexMesh.AI.Tools;

using Microsoft.Extensions.AI;

/// <summary>
/// 审批门网关抽象(实现方:NexMesh.AI.Approval 包;依赖方向:NexMesh.AI 核心不引用审批模型)。
/// 返回给模型的终局挂起文本(含审批单号)由实现方组装。
/// </summary>
public interface IToolApprovalGate
{
    /// <summary>高危工具调用入队(实现方负责幂等与上下文快照捕获),返回终局挂起文本。</summary>
    Task<string> EnqueueAsync(string toolName, string description, IReadOnlyDictionary<string, object?> arguments);
}

/// <summary>
/// 高危工具挂起包装(终局挂起语义):调用不再直达业务,而是经审批网关入队后立即返回
/// 明确的终局文本——告知模型操作已挂起待人工审批、携带审批单号、严禁重复调用或编造结果,
/// 阻断大模型对高危操作的自主闭环。
/// </summary>
public sealed class ApprovalRequiredAIFunction(AIFunction rawFunction, IToolApprovalGate gate) : AIFunction
{
    public override string Name => rawFunction.Name;

    public override string Description => rawFunction.Description;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in arguments)
        {
            args[kv.Key] = kv.Value;
        }
        return await gate.EnqueueAsync(rawFunction.Name, rawFunction.Description, args);
    }
}
