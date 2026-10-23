namespace NexMesh.AI.Resilience;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

/// <summary>NexMesh:AI:Resilience 配置节(全部有默认值,Enabled=false 可整体关闭)。</summary>
public sealed class ResilienceOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>单次模型调用超时(对流式为整个生成时长的乐观上限,靠取消令牌协作)。</summary>
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 2;
    public double BreakFailureRatio { get; set; } = 0.5;
    public int SamplingSeconds { get; set; } = 30;
    public int MinimumThroughput { get; set; } = 8;
    public int BreakSeconds { get; set; } = 15;
}

/// <summary>
/// AI 弹性管道(重试/超时/熔断,Polly v8),挂在工厂管道最内层——只包裹裸模型调用,
/// 函数调用与业务工具不在其管辖范围(30 秒超时不应击杀长工具或多轮 Agent 循环)。
/// 流式语义:重试与熔断仅覆盖"连接建立到首 token 下发"阶段;首 token 已写给调用方后
/// 不再重试(否则内容重复),异常原样上抛。流式经 Channel 生产者转发以正确穿过 Polly。
/// 管道顺序(外→内):重试 → 熔断 → 超时(每次尝试独立 30s,而非总预算)。
/// </summary>
public sealed class ResilientChatClient(IChatClient inner, ResilienceOptions options) : DelegatingChatClient(inner)
{
    private readonly ResiliencePipeline _pipeline = BuildPipeline(options);

    private static ResiliencePipeline BuildPipeline(ResilienceOptions options)
    {
        var builder = new ResiliencePipelineBuilder();
        if (options.MaxRetryAttempts > 0)
        {
            builder = builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = TimeSpan.FromSeconds(options.RetryBaseDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = args => new ValueTask<bool>(IsTransient(args.Outcome.Exception)),
            });
        }
        var pipeline = builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.BreakFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.SamplingSeconds),
                MinimumThroughput = options.MinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.BreakSeconds),
                ShouldHandle = args => new ValueTask<bool>(IsTransient(args.Outcome.Exception)),
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            })
            .Build();
        return pipeline;
    }

    /// <summary>瞬时错误判定:网络异常、Polly 超时、429 限流与 5xx。流式已开始后的失败不参与重试/熔断。</summary>
    private static bool IsTransient(Exception? exception) => exception switch
    {
        null => false,
        StreamAlreadyStartedException => false,
        HttpRequestException => true,
        TimeoutRejectedException => true,
        System.ClientModel.ClientResultException cre => cre.Status is 429 or >= 500,
        _ => false,
    };

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
        => await _pipeline.ExecuteAsync(
            async token => await base.GetResponseAsync(messages, chatOptions, token),
            cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Channel 生产者:让惰性流式枚举真正发生在 Polly 管道内部(直接赋值迭代器变量是空操作)。
        // 链接 CTS:消费方提前退出(迭代器被 Dispose)时也终止生产者,杜绝无人消费的 token 生成
        using var consumerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var producer = Task.Run(async () =>
        {
            try
            {
                var started = false;
                await _pipeline.ExecuteAsync(async token =>
                {
                    try
                    {
                        await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, token)
                                       .WithCancellation(token))
                        {
                            started = true;
                            await channel.Writer.WriteAsync(update, token);
                        }
                    }
                    catch (Exception ex) when (started)
                    {
                        // 首 token 已下发:不可重试(内容会重复),包裹为不可处理类型直通
                        throw new StreamAlreadyStartedException(ex);
                    }
                }, consumerLifetime.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(consumerLifetime.Token))
            {
                yield return update;
            }
        }
        finally
        {
            // 消费方提前退出 → 取消生产者;生产者已完成 → 本调用为空操作
            await consumerLifetime.CancelAsync();
            await producer; // 传播未观察异常
        }
    }

    private sealed class StreamAlreadyStartedException(Exception inner) : Exception("流式输出已开始,不可重试", inner);
}
