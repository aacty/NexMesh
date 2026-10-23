namespace NexMesh.Core.Middlewares;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// 全局异常中间件:X-Trace-Id 响应头 + 异常族→HTTP 状态码映射,统一 ApiResult 信封错误体
/// (success=false/code=HTTP状态码/message;InfrastructureException 及未知异常不暴露内部消息)。
/// 响应已开始(SSE 流式等)时优雅收尾而非改写响应头造成二次崩溃。
/// </summary>
public class MeshExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MeshExceptionMiddleware> _logger;

    public MeshExceptionMiddleware(RequestDelegate next, ILogger<MeshExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;

        try
        {
            await _next(context);
        }
        catch (MeshException ex) when (!context.Response.HasStarted)
        {
            _logger.LogWarning(ex, "业务异常 {TraceId} {Method} {Path} -> {StatusCode}",
                traceId, context.Request.Method, context.Request.Path, ex.StatusCode);

            await WriteErrorAsync(context, BuildError(ex, traceId));
        }
        catch (Exception ex) when (!context.Response.HasStarted
                                   && ex is not OperationCanceledException)
        {
            // SSS+安全:500 不向客户端暴露 ex.Message(可含 SQL/连接串细节);
            // 详细异常仅入日志(带 TraceId)
            _logger.LogError(ex, "未处理异常 {TraceId} {Method} {Path}",
                traceId, context.Request.Method, context.Request.Path);

            await WriteErrorAsync(context, ApiResult.Fail($"服务器内部错误(TraceId:{traceId})", 500));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端中断(SSE 流式场景高频):响应无法也不需要改写,静默收尾
        }
        catch (Exception ex)
        {
            // 响应已开始:无法改写状态码/头,记录原始异常后优雅完成响应,避免连接被重置
            _logger.LogError(ex, "响应已开始后发生未处理异常 {TraceId} {Path}", traceId, context.Request.Path);
            try
            {
                await context.Response.CompleteAsync();
            }
            catch (ObjectDisposedException)
            {
                // 连接已断开,无需收尾
            }
        }
    }

    /// <summary>异常 → 错误信封:Code=HTTP 状态码;不可暴露的消息替换为通用文案;Validation 附 Errors。</summary>
    internal static ApiResult BuildError(MeshException ex, string traceId)
    {
        var message = ex.ShouldExposeMessage
            ? ex.Message
            : $"服务器内部错误(TraceId:{traceId})";
        var error = ApiResult.Fail(message, ex.StatusCode);
        error.TraceId = traceId;
        if (ex is ValidationException { Errors.Count: > 0 } validation)
        {
            error.Errors = validation.Errors;
        }
        return error;
    }

    private static async Task WriteErrorAsync(HttpContext context, ApiResult error)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = error.Code;
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(error, JsonSerializerOptions.Web));
    }
}
