namespace NexMesh.Data.Migrations;

using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>迁移锁抽象:拿不到返回 null(调用方按策略等待或跳过);租约 Dispose 即释放。</summary>
public interface IMigrationLock
{
    /// <summary>尝试获取锁;已被他人持有时返回 null。</summary>
    Task<IMigrationLease?> TryAcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>已获取的锁租约:持有期间按半租约周期自动续期;Dispose 释放锁。</summary>
public interface IMigrationLease : IDisposable
{
}

/// <summary>
/// 迁移分布式锁(Redis 租约):多实例部署时保证同一时刻仅一个实例执行迁移,补全"单实例执行语义"的部署约束。
/// 获取:SET key token NX PX lease(随机 token 防误删他锁);持有期:半租约周期续期(Lua 校验 token);
/// 释放:Lua 校验 token 才 DEL。持有进程崩溃时租约自然过期,其他实例可接管(无死锁)。
/// </summary>
public sealed class RedisMigrationLock(
    IConnectionMultiplexer redis,
    string key = "nexmesh:migrations:lock",
    TimeSpan? lease = null,
    ILogger<RedisMigrationLock>? logger = null) : IMigrationLock
{
    private const string ReleaseScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private const string RenewScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            return 1
        end
        return 0
        """;

    private readonly TimeSpan _lease = lease ?? TimeSpan.FromSeconds(30);
    private readonly string _key = key;
    private readonly string _token = Guid.NewGuid().ToString("N");

    public async Task<IMigrationLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var db = redis.GetDatabase();
        var acquired = await db.StringSetAsync(_key, _token, _lease, When.NotExists);
        if (!acquired)
        {
            return null;
        }
        logger?.LogDebug("[NexMesh.Migrations] 分布式锁已获取(key={Key}, lease={Lease:F0}s)", _key, _lease.TotalSeconds);
        return new Lease(this, cancellationToken, _lease);
    }

    internal Task RenewAsync() =>
        redis.GetDatabase().ScriptEvaluateAsync(RenewScript,
            [_key],
            [_token, (long)_lease.TotalMilliseconds]);

    internal Task ReleaseAsync()
    {
        logger?.LogDebug("[NexMesh.Migrations] 分布式锁已释放");
        return redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [_key], [_token]);
    }

    /// <summary>租约:半租约周期续期;Dispose 停止续期并释放锁(校验 token)。</summary>
    private sealed class Lease : IMigrationLease
    {
        private readonly RedisMigrationLock _owner;
        private readonly CancellationTokenSource _cts;
        private readonly Task _renewLoop;
        private bool _disposed;

        public Lease(RedisMigrationLock owner, CancellationToken external, TimeSpan lease)
        {
            _owner = owner;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _renewLoop = RunRenewLoopAsync(lease);
        }

        private async Task RunRenewLoopAsync(TimeSpan lease)
        {
            var interval = lease / 2;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _cts.Token);
                    await _owner.RenewAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // 续期失败(Redis 闪断等)不退出循环——退出=续期停止=租约过期=另一实例双跑迁移。
                    // 下轮重试(RenewAsync 的 Lua 校验 token,幂等);仅取消令牌终止本循环。
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cts.Cancel();
            try
            {
                _renewLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 续期循环退出异常不影响释放
            }
            _owner.ReleaseAsync().GetAwaiter().GetResult();
            _cts.Dispose();
        }
    }
}

/// <summary>空锁(单实例部署/无 Redis):TryAcquire 永远成功——与无锁的既有语义完全一致。</summary>
public sealed class NoopMigrationLock : IMigrationLock
{
    public static readonly NoopMigrationLock Instance = new();

    public Task<IMigrationLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationLease?>(NoopLease.Instance);

    private sealed class NoopLease : IMigrationLease
    {
        public static readonly NoopLease Instance = new();

        public void Dispose()
        {
        }
    }
}
