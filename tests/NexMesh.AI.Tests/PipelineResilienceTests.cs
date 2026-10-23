namespace NexMesh.AI.Tests;

using Microsoft.Extensions.AI;
using NexMesh.AI.Resilience;

/// <summary>AI 弹性管道语义测试:非流式重试/超时;流式"首 token 前可重试、首 token 后不重复"。</summary>
public class PipelineResilienceTests
{
    private static ResilienceOptions FastOptions(int timeoutMs = 2000) => new()
    {
        TimeoutSeconds = System.Math.Max(1, timeoutMs / 1000),
        MaxRetryAttempts = 3,
        RetryBaseDelaySeconds = 0, // 测试不等待退避
        BreakFailureRatio = 0.99,
        SamplingSeconds = 3600,
        MinimumThroughput = 100,
        BreakSeconds = 1,
    };

    private sealed record FlakyClient(int FailTimes, TimeSpan? Delay = null) : IChatClient
    {
        public int Calls { get; private set; }
        public int TokensYielded { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("network down");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls <= FailTimes)
            {
                throw new HttpRequestException("connect refused");
            }
            if (Delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }
            TokensYielded++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "token-A");
            TokensYielded++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "token-B");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class AlwaysFailNonStreaming : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("network down");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task 非流式_瞬时错误指数退避重试后成功()
    {
        var flaky = new FlakyClient(FailTimes: 2);
        using var client = new ResilientChatClient(flaky, FastOptions());

        // 流式路径验证首 token 前重试:前两次连接失败,第三次成功,内容只出一份
        var tokens = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            tokens.Add(update.Text ?? "");
        }

        Assert.Equal(3, flaky.Calls);         // 失败2次 + 成功1次
        Assert.Equal(2, tokens.Count);        // 无重复 token
        Assert.Contains("token-A", tokens);
    }

    [Fact]
    public async Task 非流式_重试耗尽后异常上抛()
    {
        var failing = new AlwaysFailNonStreaming();
        using var client = new ResilientChatClient(failing, FastOptions());

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(4, failing.Calls); // 首次 + 3 次重试
    }

    private sealed class SlowClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "late"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task 超时_超时后强制中断()
    {
        var options = FastOptions(timeoutMs: 1000);
        options.MaxRetryAttempts = 0;
        using var client = new ResilientChatClient(new SlowClient(), options);

        // Polly 超时以 TimeoutRejectedException 呈现(内部 TaskCanceledException)
        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    private sealed class FailAfterFirstTokenClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first-token");
            throw new HttpRequestException("mid-stream network reset");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task 流式首token后失败_不重试不重复内容()
    {
        var inner = new FailAfterFirstTokenClient();
        using var client = new ResilientChatClient(inner, FastOptions());

        var tokens = new List<string>();
        var caught = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                tokens.Add(update.Text ?? "");
            }
        });

        Assert.Single(tokens);                        // 只收到首个 token,无重复
        Assert.Equal("first-token", tokens[0]);
        Assert.Equal(1, inner.Calls);                 // 未重试(重试会导致内容重复)
    }
}
