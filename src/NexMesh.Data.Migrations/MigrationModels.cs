namespace NexMesh.Data.Migrations;

using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using FreeSql;
using FreeSql.DataAnnotations;

/// <summary>
/// 迁移单元基类:业务继承并声明 Version(同源内升序执行)与 UpAsync(建表/改表/种子)。
/// 生产环境用显式迁移链替代 AutoSyncStructure——变更可评审、可回滚、可审计。
/// 多数据源:默认 Source="main"(经 IDbFactory 解析);类型化数据源(IFreeSql&lt;TDBSource&gt;)覆写 ResolveDb。
/// </summary>
public abstract class NexMeshMigration
{
    /// <summary>版本号(同源内必须唯一,升序执行;推荐 yyyymmddHHmm 或递增序号)。</summary>
    public abstract long Version { get; }

    /// <summary>迁移名称(记录表展示与审计)。</summary>
    public abstract string Name { get; }

    /// <summary>目标数据源名称(IDbFactory.Get(Source);默认 main)。</summary>
    public virtual string Source => "main";

    /// <summary>应用迁移(建表/加列/数据变更;在独立事务内执行)。</summary>
    public abstract Task UpAsync(IFreeSql db, CancellationToken cancellationToken = default);

    /// <summary>回滚迁移(可选;经 RevertAsync 手动触发,不会自动执行)。</summary>
    public virtual Task DownAsync(IFreeSql db, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>解析目标库:默认按 Source 名称经 IDbFactory;类型化数据源场景覆写(如 sp.GetRequiredService&lt;IFreeSql&lt;ReportDBSource&gt;&gt;())。</summary>
    public virtual IFreeSql ResolveDb(IServiceProvider services)
        => services.GetRequiredService<NexMesh.Data.IDbFactory>().Get(Source);

    /// <summary>迁移指纹(版本|名称|类型全名 的 SHA256):已发布迁移被改动时启动报错,防篡改历史。</summary>
    public string Checksum
    {
        get
        {
            var raw = $"{Version}|{Name}|{GetType().FullName}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }
    }
}

/// <summary>迁移记录表(框架自建,表名固定 nexmesh_migration)。</summary>
[Table(Name = "nexmesh_migration")]
[Index("ux_nexmesh_migration_source_version", "source,version", true)]
public class MigrationRecordEntity
{
    [Column(Name = "id", IsPrimary = true, IsIdentity = true)]
    public long Id { get; set; }

    /// <summary>目标数据源名称。</summary>
    [Column(Name = "source", StringLength = 64)]
    public string Source { get; set; } = "main";

    /// <summary>迁移版本号(同源内唯一)。</summary>
    [Column(Name = "version")]
    public long Version { get; set; }

    /// <summary>迁移名称。</summary>
    [Column(Name = "name", StringLength = 256)]
    public string Name { get; set; } = "";

    /// <summary>应用时的迁移指纹(再次启动校验一致,不一致=已发布迁移被改动)。</summary>
    [Column(Name = "checksum", StringLength = 128)]
    public string Checksum { get; set; } = "";

    /// <summary>应用时间。</summary>
    [Column(Name = "applied_at")]
    public DateTime AppliedAt { get; set; }

    /// <summary>执行耗时(毫秒)。</summary>
    [Column(Name = "duration_ms")]
    public int DurationMs { get; set; }
}

/// <summary>迁移配置节(NexMesh:Migrations)。</summary>
public sealed class NexMeshMigrationsOptions
{
    public const string SectionName = "NexMesh:Migrations";

    /// <summary>启动期自动执行待应用迁移(默认 false——高危操作显式开启;
    /// 需要与模块种子精确编排顺序时改用显式 app.MigrateNexMeshAsync())。</summary>
    public bool Enabled { get; set; }
}

/// <summary>迁移执行器:校验(重复版本/指纹篡改)→ 按源分组 → 升序应用(单迁移单事务)。</summary>
public interface IMigrationRunner
{
    /// <summary>应用全部待迁移,返回本次应用条数;任何失败抛出(回滚当条,fail-fast)。</summary>
    Task<int> RunPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>回滚指定版本(执行 DownAsync 并删记录);不会自动触发。</summary>
    Task RevertAsync(string source, long version, CancellationToken cancellationToken = default);

    /// <summary>列出已应用迁移(Dashboard 展示;按源分组升序)。库不可达的源返回空,不抛出。</summary>
    Task<List<MigrationSummary>> ListAppliedAsync(CancellationToken cancellationToken = default);

    /// <summary>列出待应用迁移(版本/名称/源;不执行——Dashboard 预览用)。库不可达的源返回空。</summary>
    Task<List<MigrationSummary>> ListPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>迁移记录摘要(Dashboard 展示投影,不含 Checksum)。</summary>
public sealed record MigrationSummary(string Source, long Version, string Name, DateTime AppliedAt, int DurationMs);
