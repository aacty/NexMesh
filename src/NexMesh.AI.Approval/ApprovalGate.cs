namespace NexMesh.AI.Approval;

using System.Collections.Concurrent;
using System.Text.Json;
using NexMesh.AI.Tools;
using NexMesh.Core.Context;

/// <summary>
/// 幂等入队网关(NexMesh.AI 审批门抽象的实现):
/// 捕获触发时刻的上下文快照消息头随审批单持久化(审批跨越数小时,批准后按快照再水化执行,
/// 身份与租户不变);同参数重复请求合并为既有审批单;返回给模型的终局挂起文本在此组装。
/// SSS+交叉审核修复:查重+入队经 64 条带锁串行化(按幂等键哈希分条)——修复 TOCTOU
/// (并发同参调用曾产生多张待审单,逐张批准即高危工具双执行;实证:48 并发 20 张单)。
/// 多实例部署经共享库时仍可能跨进程竞态(条带锁为进程内语义),彻底防线需按库建
/// `IdempotencyKey WHERE Status=Pending` 的部分唯一索引(各库语法不一,留作部署加固项)。
/// </summary>
public sealed class ApprovalGate(IApprovalStore store, INexMeshContextAccessor contextAccessor) : IToolApprovalGate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int StripCount = 64;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Stripes = new();

    public async Task<string> EnqueueAsync(
        string toolName, string description, IReadOnlyDictionary<string, object?> arguments)
    {
        var argumentsJson = JsonSerializer.Serialize(arguments, Json);
        var idempotencyKey = ApprovalRequest.ComputeIdempotencyKey(toolName, argumentsJson);

        // 条带锁:同键查重+入队原子化(跨键不互斥);条带按哈希取模,内存有界无清扫
        var stripe = Stripes.GetOrAdd(Math.Abs(idempotencyKey.GetHashCode()) % StripCount, _ => new SemaphoreSlim(1, 1));
        await stripe.WaitAsync();
        try
        {
            var existing = await store.FindPendingByIdempotencyKeyAsync(idempotencyKey);
            if (existing is not null)
            {
                return $"[NexMesh Approval Gate] 高危操作 {toolName} 的审批单 {existing.Id} 已在等待中(重复请求已合并)。" +
                       "请停止重复调用,直接告知用户该操作需要人工审批;严禁编造执行结果。";
            }

            var request = new ApprovalRequest
            {
                ToolName = toolName,
                Description = description,
                ArgumentsJson = argumentsJson,
                IdempotencyKey = idempotencyKey,
                ContextHeaders = contextAccessor.Current?.ToMessageHeaders() ?? new Dictionary<string, string>(),
            };
            await store.AddAsync(request);

            return $"[NexMesh Approval Gate] 高危操作 {toolName} 已挂起,审批单号 {request.Id},等待管理员审批后方可执行。" +
                   "请立即停止对本工具的重复调用,直接告知用户该操作需要人工审批;严禁编造执行结果。";
        }
        finally
        {
            stripe.Release();
        }
    }
}
