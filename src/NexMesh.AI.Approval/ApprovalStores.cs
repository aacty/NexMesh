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

    public Task<List<ApprovalRequest>> ListHistoryAsync(int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.Values
            .Where(r => r.Status != ApprovalStatus.Pending)
            .OrderByDescending(r => r.DecidedAt ?? r.CreatedAt)
            .Take(limit)
            .ToList());

    public Task<ApprovalRequest?> ClaimForDecisionAsync(string id, ApprovalStatus decision, string decidedBy)
    {
        // 引用相等 CAS:并发决策恰好一人成功;重试上限 64 次(持续争持 fail-closed 判负——高危工具宁可拒不可双执行)
        for (var attempt = 0; attempt < 64; attempt++)
        {
            if (!_store.TryGetValue(id, out var current) || current.Status != ApprovalStatus.Pending)
            {
                return Task.FromResult<ApprovalRequest?>(null);
            }
            var claimed = current with { Status = decision, DecidedAt = DateTime.Now, DecidedBy = decidedBy };
            if (_store.TryUpdate(id, claimed, current))
            {
                return Task.FromResult<ApprovalRequest?>(claimed);
            }
        }
        return Task.FromResult<ApprovalRequest?>(null);
    }
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

    public async Task<ApprovalRequest?> ClaimForDecisionAsync(string id, ApprovalStatus decision, string decidedBy)
    {
        await _tableSync.Value;
        // 单语句原子认领:WHERE status=pending 守卫,影响行数即胜负——并发决策恰好一人成功
        var affected = await db.Update<ApprovalRequestEntity>()
            .Set(e => e.Status, decision)
            .Set(e => e.DecidedAt, DateTime.Now)
            .Set(e => e.DecidedBy, decidedBy)
            .Where(e => e.Id == id && e.Status == ApprovalStatus.Pending)
            .ExecuteAffrowsAsync();
        if (affected == 0)
        {
            return null; // 不存在或已被他人认领
        }
        var entity = await db.Select<ApprovalRequestEntity>().Where(e => e.Id == id).FirstAsync();
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<List<ApprovalRequest>> ListHistoryAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return []; // SSS+审核:FreeSql Take(≤0) 会忽略 LIMIT(实测 SQLite 返回全表)——契约要求"取前 limit 条",≤0 显式守卫为空表
        }
        await _tableSync.Value;
        var entities = await db.Select<ApprovalRequestEntity>()
            .Where(e => e.Status != ApprovalStatus.Pending)
            .OrderByDescending(e => e.DecidedAt ?? e.CreatedAt)
            .Take(limit)
            .ToListAsync();
        return entities.Select(ToRecord).ToList();
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
