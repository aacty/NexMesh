
namespace NexMesh.Data;

/// <summary>
/// 实体约定接口:实现即自动接管(SPD 平权,对应旧 CloudEngine 的 IDelete/EntityKeyLongBase 手工时代)。
/// </summary>

/// <summary>软删除:全局过滤器自动排除 IsDeleted 记录,仓储 SoftDeleteAsync 只标记不物理删。</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
}

/// <summary>时间审计:仓储写入时自动填充 CreateTime/UpdateTime。</summary>
public interface IAudited
{
    DateTime CreateTime { get; set; }
    DateTime? UpdateTime { get; set; }
}

/// <summary>用户审计(可单独实现):从 ICurrentUser 自动填充操作人。</summary>
public interface IUserAudited
{
    string? CreateUserId { get; set; }
    string? UpdateUserId { get; set; }
}

/// <summary>乐观并发:仓储更新校验戳,冲突抛 MeshException(40902/409)。</summary>
public interface IHasConcurrencyStamp
{
    string ConcurrencyStamp { get; set; }
}

/// <summary>
/// 多租户/多院区隔离(SSS+安全修复):实现此接口的实体,框架自动挂全局过滤——
/// 查询只返回 ICurrentUser.TenantId 匹配的记录,写入自动填充 TenantId。
/// 一处遗漏即跨院区数据泄露的系统性风险由此封死。
/// </summary>
public interface ITenantEntity
{
    string TenantId { get; set; }
}

/// <summary>命名库配置(AddNexMeshDb)。</summary>
public sealed class NexMeshDbOptions
{
    public FreeSql.DataType DataType { get; set; } = FreeSql.DataType.Sqlite;
    public string ConnectionString { get; set; } = "Data Source=app.db";
    /// <summary>CodeFirst 自动建表;null = 按环境(开发期 true)。</summary>
    public bool? AutoSyncStructure { get; set; }
    /// <summary>软删全局过滤(默认开)。</summary>
    public bool EnableSoftDeleteFilter { get; set; } = true;
    /// <summary>租户全局过滤(SSS+安全:ITenantEntity 实体自动限定 ICurrentUser.TenantId;默认开)。</summary>
    public bool EnableTenantFilter { get; set; } = true;
    /// <summary>雪花 ID 的节点号(集群内唯一,10 位)。</summary>
    public int WorkerId { get; set; } = 1;
}

/// <summary>雪花 ID 生成(41 位毫秒时间戳 + 10 位节点 + 12 位序列;epoch 2026-01-01,约 69 年)。</summary>
public static class NexMeshSnowflake
{
    private const long Epoch = 1767225600000L; // 2026-01-01T00:00:00Z
    private const int WorkerBits = 10;
    private const int SeqBits = 12;
    private const long MaxWorker = (1L << WorkerBits) - 1;

    private static long _workerId = 1;
    private static long _lastTimestamp;
    private static long _sequence;
    private static readonly object Gate = new();

    public static void Configure(int workerId)
    {
        if (workerId is < 0 or > (int)MaxWorker)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), $"WorkerId 必须在 0~{MaxWorker} 之间");
        }
        _workerId = workerId;
    }

    public static long NextId()
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < _lastTimestamp)
            {
                now = _lastTimestamp; // 时钟回拨:沿用上次时间戳等待序列耗尽,保守不失序
            }
            if (now == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & ((1L << SeqBits) - 1);
                if (_sequence == 0)
                {
                    while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= _lastTimestamp)
                    {
                        Thread.Sleep(1);
                    }
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            else
            {
                _sequence = 0;
            }
            _lastTimestamp = now;
            return ((now - Epoch) << (WorkerBits + SeqBits)) | (_workerId << SeqBits) | _sequence;
        }
    }
}
