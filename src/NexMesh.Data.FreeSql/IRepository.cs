namespace NexMesh.Data;

using System.Linq.Expressions;
using System.Reflection;
using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

/// <summary>
/// 仓储:实体实现约定接口即自动接管——
/// 雪花 ID(long 主键为 0 时)、时间审计(IAudited)、用户审计(IUserAudited,取 ICurrentUser)、
/// 乐观并发(IHasConcurrencyStamp,戳不匹配 → 409)、软删(ISoftDelete,只标记 + 全局过滤)。
/// 复杂查询直接注入 IFreeSql(全局过滤同样生效)。
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T> AddAsync(T entity, CancellationToken ct = default);
    Task<int> AddManyAsync(IEnumerable<T> entities, CancellationToken ct = default);
    Task<T?> FindAsync(object id, CancellationToken ct = default);
    Task<T> UpdateAsync(T entity, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(object id, CancellationToken ct = default);
    Task<long> CountAsync(Expression<Func<T, bool>>? where = null, CancellationToken ct = default);
    Task<List<T>> ListAsync(Expression<Func<T, bool>>? where = null, CancellationToken ct = default);
    Task<PagedResult<T>> PagedAsync(int pageIndex, int pageSize, Expression<Func<T, bool>>? where = null, CancellationToken ct = default);
}

public sealed class Repository<T>(IDbFactory dbFactory, ICurrentUser? user) : IRepository<T> where T : class
{
    private IFreeSql Db => dbFactory.Get();

    public async Task<T> AddAsync(T entity, CancellationToken ct = default)
    {
        FillCreate(entity);
        // 自增主键:ExecuteIdentity 显式取回数据库分配值(雪花已处理的非自增主键返回 0 不覆盖)
        var identity = await Db.Insert(entity).ExecuteIdentityAsync(ct);
        if (identity > 0)
        {
            SetId(entity, identity);
        }
        return entity;
    }

    public async Task<int> AddManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities.ToList();
        foreach (var e in list)
        {
            FillCreate(e);
        }
        return await Db.Insert(list).ExecuteAffrowsAsync(ct);
    }

    public async Task<T?> FindAsync(object id, CancellationToken ct = default) =>
        await Db.Select<T>().WhereDynamic(id).FirstAsync(ct);

    public async Task<T> UpdateAsync(T entity, CancellationToken ct = default)
    {
        FillUpdate(entity);

        if (entity is IHasConcurrencyStamp stamped)
        {
            // 乐观并发:取库中当前戳比对,不匹配 → 409(先读后写的窄窗口换取零反射成本,注释即契约)
            var id = GetId(entity);
            var current = await Db.Select<T>().WhereDynamic(id).FirstAsync(ct);
            if (current is IHasConcurrencyStamp currentStamped
                && currentStamped.ConcurrencyStamp != stamped.ConcurrencyStamp)
            {
                throw new MeshException("数据已被他人修改,请刷新后重试(乐观并发冲突)", 40902, 409);
            }
            stamped.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        }

        await Db.Update<T>().SetSource(entity).ExecuteAffrowsAsync(ct);
        return entity;
    }

    public async Task<bool> SoftDeleteAsync(object id, CancellationToken ct = default)
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
        {
            throw new NotSupportedException($"实体 {typeof(T).Name} 未实现 ISoftDelete,不能软删除");
        }
        var entity = await Db.Select<T>().WhereDynamic(id).FirstAsync(ct);
        if (entity is not ISoftDelete soft)
        {
            return false;
        }
        soft.IsDeleted = true;
        if (entity is IAudited audited)
        {
            audited.UpdateTime = DateTime.Now;
        }
        if (entity is IUserAudited userAudited)
        {
            userAudited.UpdateUserId = user?.UserId;
        }
        return await Db.Update<T>().SetSource(entity).ExecuteAffrowsAsync(ct) > 0;
    }

    public Task<long> CountAsync(Expression<Func<T, bool>>? where = null, CancellationToken ct = default) =>
        where is null ? Db.Select<T>().CountAsync(ct) : Db.Select<T>().Where(where).CountAsync(ct);

    public Task<List<T>> ListAsync(Expression<Func<T, bool>>? where = null, CancellationToken ct = default) =>
        where is null ? Db.Select<T>().ToListAsync(ct) : Db.Select<T>().Where(where).ToListAsync(ct);

    public async Task<PagedResult<T>> PagedAsync(
        int pageIndex, int pageSize, Expression<Func<T, bool>>? where = null, CancellationToken ct = default)
    {
        var query = where is null ? Db.Select<T>() : Db.Select<T>().Where(where);
        var total = await query.CountAsync(ct);
        var items = await query.Page(pageIndex, pageSize).ToListAsync(ct);
        return new PagedResult<T> { TotalCount = total, Items = items, PageIndex = pageIndex, PageSize = pageSize };
    }

    private void FillCreate(T entity)
    {
        if (entity is ISoftDelete soft)
        {
            soft.IsDeleted = false;
        }
        if (entity is IAudited audited)
        {
            audited.CreateTime = DateTime.Now;
        }
        if (entity is IUserAudited userAudited)
        {
            userAudited.CreateUserId = user?.UserId;
        }
        // SSS+第二轮修复(S4 契约补齐):租户实体写入自动填充 TenantId——
        // 业务不填时取当前用户租户,再退 AsyncLocal 租户槽;两者皆无则保持 null
        // (归"未指定租户"集,与 fail-closed 过滤的 IS NULL 语义对齐)
        if (entity is ITenantEntity tenantEntity && string.IsNullOrEmpty(tenantEntity.TenantId))
        {
            var tenant = user?.TenantId ?? Core.Context.TenantHelper.Current;
            if (!string.IsNullOrEmpty(tenant))
            {
                tenantEntity.TenantId = tenant;
            }
        }
        if (entity is IHasConcurrencyStamp stamped && string.IsNullOrEmpty(stamped.ConcurrencyStamp))
        {
            stamped.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        }
        // 非自增 long 主键为 0 → 雪花接管;自增主键([Column(IsIdentity=true)])由数据库分配,严禁预填
        if (GetId(entity) is long { } rawId && rawId == 0 && !IsIdentityId())
        {
            SetId(entity, NexMeshSnowflake.NextId());
        }
    }

    private static bool IsIdentityId() =>
        typeof(T).GetProperty("Id")?.GetCustomAttribute<FreeSql.DataAnnotations.ColumnAttribute>()
            ?.IsIdentity == true;

    private void FillUpdate(T entity)
    {
        if (entity is IAudited audited)
        {
            audited.UpdateTime = DateTime.Now;
        }
        if (entity is IUserAudited userAudited)
        {
            userAudited.UpdateUserId = user?.UserId;
        }
    }

    private static object? GetId(T entity) =>
        typeof(T).GetProperty("Id")?.GetValue(entity);

    private static void SetId(T entity, long value) =>
        typeof(T).GetProperty("Id")?.SetValue(entity, value);
}
