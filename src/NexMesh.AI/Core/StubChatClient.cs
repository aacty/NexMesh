namespace NexMesh.AI.Core;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// 离线 Stub Provider(Type=stub):固定回复、模拟流式,不发起任何网络请求。
/// 用于本地开发/演示/测试——无 ApiKey 也能跑通全链路。
/// </summary>
public sealed class StubChatClient(string model = "nexmesh-stub") : IChatClient
{
    private readonly string _model = string.IsNullOrEmpty(model) ? "nexmesh-stub" : model;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var count = messages.Count();
        var text = $"[NexMesh Stub:{_model}] 收到 {count} 条消息。当前为离线 Stub Provider," +
                   $"在 NexMesh:AI:Providers 配置真实 Provider(OpenAI/DeepSeek/Ollama)后即可获得模型回复。";
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = _model,
            Usage = new UsageDetails
            {
                InputTokenCount = count,
                OutputTokenCount = 16,
                TotalTokenCount = count + 16,
            },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunks = new[] { "[NexMesh Stub] ", "这是一段", "离线流式", "演示输出。" };
        foreach (var chunk in chunks)
        {
            await Task.Delay(50, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
