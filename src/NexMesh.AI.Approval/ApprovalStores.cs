using System.Collections.Concurrent;
using FreeSql;
using FreeSql.DataAnnotations;

namespace NexMesh.AI.Approval;

/// <summary>一次性异步初始化(建表)防重入。</summary>
internal sealed class AsyncLazy(Func<Task> factory)
{
    private readonly Task _task = factory();
    public Task Value => _task;
}

/// <summary>内存审批队列(开发/测试默认;重启即失,生产用 FreeSqlApprovalStore)。</summary>
public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<ApprovalRequest> AddAsync(ApprovalRequest request)
    {
        _store[request.Id] = request;
        return Task.FromResult(request);
    }

    public Task<ApprovalRequest?> GetAsync(string id) =>
        Task.FromResult(_store.TryGetValue(id, out var request) ? request : null);

    public Task<List<ApprovalRequest>> ListPendingAsync() =>
        Task.FromResult(_store.Values
            .Where(r => r.Status == ApprovalStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToList());

    public Task<ApprovalRequest> UpdateAsync(ApprovalRequest request)
    {
        _store[request.Id] = request;
        return Task.FromResult(request);
    }

    public Task<ApprovalRequest?> FindPendingByIdempotencyKeyAsync(string idempotencyKey) =>
        Task.FromResult(_store.Values.FirstOrDefault(
            r => r.Status == ApprovalStatus.Pending && r.IdempotencyKey == idempotencyKey));
}

/// <summary>审批队列持久化实体(FreeSql CodeFirst,表 nexmesh_approvals)。</summary>
[Table(Name = "nexmesh_approvals")]
public class ApprovalRequestEntity
{
    [Column(IsPrimary = true, StringLength = 64)]
    public string Id { get; set; } = "";

    [Column(StringLength = 256)]
    public string ToolName { get; set; } = "";

    [Column(StringLength = 1024)]
    public string Description { get; set; } = "";

    [Column(StringLength = -1)]
    public string ArgumentsJson { get; set; } = "{}";

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    [Column(StringLength = 128)]
    public string? DecidedBy { get; set; }

    [Column(StringLength = -1)]
    public string? Result { get; set; }

    [Column(StringLength = -1)]
    public string? Error { get; set; }

    [Column(StringLength = -1)]
    public string ContextHeadersJson { get; set; } = "{}";

    [Column(StringLength = 128)]
    public string IdempotencyKey { get; set; } = "";
}

/// <summary>FreeSql 审批队列(生产):事务库一张表,审批记录可审计可追溯。</summary>
public sealed class FreeSqlApprovalStore(IFreeSql db) : IApprovalStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly AsyncLazy _tableSync = new(() => Task.Run(() => db.CodeFirst.SyncStructure<ApprovalRequestEntity>()));

    public async Task<ApprovalRequest> AddAsync(ApprovalRequest request)
    {
        await _tableSync.Value;
        await db.Insert(ToEntity(request)).ExecuteAffrowsAsync();
        return request;
    }

    public async Task<ApprovalRequest?> GetAsync(string id)
    {
        await _tableSync.Value;
        var entity = await db.Select<ApprovalRequestEntity>().Where(e => e.Id == id).FirstAsync();
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<List<ApprovalRequest>> ListPendingAsync()
    {
        await _tableSync.Value;
        var entities = await db.Select<ApprovalRequestEntity>()
            .Where(e => e.Status == ApprovalStatus.Pending)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
        return entities.Select(ToRecord).ToList();
    }

    public async Task<ApprovalRequest> UpdateAsync(ApprovalRequest request)
    {
        await _tableSync.Value;
        await db.Update<ApprovalRequestEntity>()
            .Set(e => e.Status, request.Status)
            .Set(e => e.DecidedAt, request.DecidedAt)
            .Set(e => e.DecidedBy, request.DecidedBy)
            .Set(e => e.Result, request.Result)
            .Set(e => e.Error, request.Error)
            .Where(e => e.Id == request.Id)
            .ExecuteAffrowsAsync();
        return request;
    }

    public async Task<ApprovalRequest?> FindPendingByIdempotencyKeyAsync(string idempotencyKey)
    {
        await _tableSync.Value;
        var entity = await db.Select<ApprovalRequestEntity>()
            .Where(e => e.Status == ApprovalStatus.Pending && e.IdempotencyKey == idempotencyKey)
            .FirstAsync();
        return entity is null ? null : ToRecord(entity);
    }

    private static ApprovalRequestEntity ToEntity(ApprovalRequest r) => new()
    {
        Id = r.Id,
        ToolName = r.ToolName,
        Description = r.Description,
        ArgumentsJson = r.ArgumentsJson,
        Status = r.Status,
        CreatedAt = r.CreatedAt,
        DecidedAt = r.DecidedAt,
        DecidedBy = r.DecidedBy,
        Result = r.Result,
        Error = r.Error,
        ContextHeadersJson = System.Text.Json.JsonSerializer.Serialize(r.ContextHeaders, Json),
        IdempotencyKey = r.IdempotencyKey,
    };

    private static ApprovalRequest ToRecord(ApprovalRequestEntity e) => new()
    {
        Id = e.Id,
        ToolName = e.ToolName,
        Description = e.Description,
        ArgumentsJson = e.ArgumentsJson,
        Status = e.Status,
        CreatedAt = e.CreatedAt,
        DecidedAt = e.DecidedAt,
        DecidedBy = e.DecidedBy,
        Result = e.Result,
        Error = e.Error,
        ContextHeaders = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(e.ContextHeadersJson, Json) ?? [],
        IdempotencyKey = e.IdempotencyKey,
    };
}
