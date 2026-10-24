namespace NexMesh.Events;

/// <summary>
/// 重试执行环:特性级重试的唯一实现(分发路径与测试共用同一真实现)。
/// 语义:尝试上限 = NumRetries + 1;第 attempt 次失败且还有额度且异常在白名单内 → 延迟后重试;
/// 返回 null=成功,非 null=最终失败异常(重试耗尽或白名单外)。
/// 取消语义:①取消(OperationCanceledException / 令牌已取消)是控制流信号而非故障,
/// 终态返回、不重试;②重试延迟尊重取消令牌。
/// </summary>
internal static class EventRetryLoop
{
    public static async Task<Exception?> ExecuteAsync(
        int numRetries,
        int retryTimeoutMs,
        Type[]? exceptionTypes,
        TimeProvider timeProvider,
        Func<Task> action,
        Action? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        var attempts = numRetries + 1;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception ex)
            {
                // 取消是控制流不是故障:终态返回,永不重试
                if (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    return ex;
                }
                lastError = ex;
                if (attempt >= attempts || !ShouldRetry(exceptionTypes, ex))
                {
                    return lastError;
                }
                onRetry?.Invoke();
                if (retryTimeoutMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(retryTimeoutMs), timeProvider, cancellationToken);
                }
            }
        }
        return lastError;
    }

    /// <summary>仅当异常类型在白名单内(或白名单为空=全部)才重试。</summary>
    public static bool ShouldRetry(Type[]? exceptionTypes, Exception exception)
    {
        if (exceptionTypes is not { Length: > 0 })
        {
            return true;
        }
        return exceptionTypes.Any(t => t.IsInstanceOfType(exception));
    }
}
