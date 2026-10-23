namespace NexMesh.AI.Core;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

/// <summary>
/// 断连熔断守卫(大模型吸血鬼效应防御):出站 LLM 调用的取消令牌无条件链接
/// HttpContext.RequestAborted——用户关闭浏览器/刷新页面的瞬间,下游模型流、
/// 函数调用与计费立即随请求终止;请求上下文之外(离线任务)自动退化为透传,
/// 由 BackgroundRunner 的停机令牌接管。挂在工厂管道最外层,业务无从绕过。
/// </summary>
public sealed class NexMeshCancellationGuardChatClient(
    IChatClient inner,
    IHttpContextAccessor? httpAccessor) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var linked = LinkToRequest(cancellationToken);
        try
        {
            return await base.GetResponseAsync(messages, options, linked?.Token ?? cancellationToken);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var linked = LinkToRequest(cancellationToken);
        var token = linked?.Token ?? cancellationToken;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, token))
            {
                yield return update;
            }
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <summary>请求内:调用方令牌 + RequestAborted 双链接;请求外(离线):仅调用方令牌。</summary>
    private CancellationTokenSource? LinkToRequest(CancellationToken cancellationToken)
    {
        var requestAborted = httpAccessor?.HttpContext?.RequestAborted;
        if (requestAborted is not { CanBeCanceled: true } || cancellationToken == requestAborted)
        {
            return null;
        }
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestAborted.Value);
    }
}
