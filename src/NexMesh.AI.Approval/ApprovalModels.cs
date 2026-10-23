namespace NexMesh.AI.Approval;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>审批状态机:Pending → (Approved → Executed/Failed) | Rejected。</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Executed = 3,
    Failed = 4,
}

/// <summary>
/// 审批请求(持久化形态):高危工具的调用参数 + 触发时的上下文快照消息头 + 幂等键。
/// 批准后由决策服务按快照再水化身份、在独立作用域执行原始工具。
/// </summary>
public sealed record ApprovalRequest
{
    public string Id { get; init; } = Guid.CreateVersion7().ToString("N");
    public string ToolName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }

    /// <summary>触发时的上下文快照消息头(身份/租户/链路),执行时再水化——审批跨越数小时,身份租户不变。</summary>
    public IReadOnlyDictionary<string, string> ContextHeaders { get; init; } = new Dictionary<string, string>();

    /// <summary>幂等键:ToolName + 参数内容 SHA256——模型重复请求同一高危操作不产生重复审批单。</summary>
    public string IdempotencyKey { get; init; } = "";

    public static string ComputeIdempotencyKey(string toolName, string argumentsJson)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(toolName + "\n" + argumentsJson));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>审批队列持久化(内存实现用于开发/测试;FreeSql 实现用于生产)。</summary>
public interface IApprovalStore
{
    Task<ApprovalRequest> AddAsync(ApprovalRequest request);
    Task<ApprovalRequest?> GetAsync(string id);
    Task<List<ApprovalRequest>> ListPendingAsync();
    Task<ApprovalRequest> UpdateAsync(ApprovalRequest request);
    Task<ApprovalRequest?> FindPendingByIdempotencyKeyAsync(string idempotencyKey);
}
