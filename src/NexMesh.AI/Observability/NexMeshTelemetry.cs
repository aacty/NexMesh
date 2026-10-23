namespace NexMesh.AI.Observability;

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexMesh.AI.Tools;

/// <summary>NexMesh.AI 遥测源(OpenTelemetry gen_ai 语义约定)。</summary>
public static class NexMeshTelemetry
{
    public const string ActivitySourceName = "NexMesh.AI";

    public static ActivitySource Source { get; } = new(ActivitySourceName);
}

/// <summary>
/// 工具分发包装(静态与实例工具统一经过):调用期 gen_ai 遥测 + 大模型输出错配的自纠错防线——
/// 反序列化/格式错误(日期串、漏必填、枚举大小写)与执行异常不再击穿 Agent 循环,
/// 而是转为结构化错误文本作为工具结果回喂大模型,由模型反思修正参数后重新调用。
/// 异常同时记录日志(自纠错不等于静默吞错,编程缺陷必须在日志可见)。
/// </summary>
internal sealed class TelemetryAIFunction(AIFunction inner, NexMeshRiskLevel risk, ILogger? logger) : AIFunction
{
    public override string Name => inner.Name;

    public override string Description => inner.Description;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var activity = NexMeshTelemetry.Source.StartActivity("gen_ai.execute_tool");
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "execute_tool");
            activity.SetTag("gen_ai.tool.name", inner.Name);
            activity.SetTag("nexmesh.tool.risk_level", risk.ToString());
        }

        try
        {
            return await inner.InvokeAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("nexmesh.tool.canceled", true);
            throw; // 取消必须上抛:断连熔断与停机语义依赖它
        }
        catch (Exception ex)
        {
            // 自纠错防线:大模型参数错配是常态,转为可反思的错误结果而非崩溃。
            // B7 安全修复:回喂文本只含异常类型(不泄露 ex.Message——SQL/连接串/内部路径
            // 会经外部 LLM 提供商外泄);详细异常仅入日志。
            activity?.SetTag("nexmesh.tool.error", ex.GetType().Name);
            logger?.LogError(ex, "工具 {ToolName} 调用失败(已转为自纠错结果回喂模型)", inner.Name);
            return
                $"[NexMesh Tool Error] 工具 {inner.Name} 调用失败({ex.GetType().Name})。" +
                "请检查参数格式(日期使用 yyyy-MM-dd、必填字段不可缺省、枚举值与定义一致)并修正后重新调用;" +
                "若无法修正,请直接向用户说明该操作暂不可用,禁止编造结果。";
        }
    }
}
