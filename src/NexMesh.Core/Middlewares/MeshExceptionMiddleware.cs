namespace NexMesh.Core.Middlewares;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

            if (await InvokeHandlersAsync(context, ex, BuildError(ex, traceId)))
            {
                return; // IsTerminating 处理器已接管响应写出
            }
            await WriteErrorAsync(context, BuildError(ex, traceId), ex.StatusCode);
        }
        catch (Exception ex) when (!context.Response.HasStarted
                                   && ex is not OperationCanceledException)
        {
            // SSS+安全:500 不向客户端暴露 ex.Message(可含 SQL/连接串细节);
            // 详细异常仅入日志(带 TraceId)
            _logger.LogError(ex, "未处理异常 {TraceId} {Method} {Path}",
                traceId, context.Request.Method, context.Request.Path);

            var fallback = ApiResult.Fail($"服务器内部错误(TraceId:{traceId})", 50000);
            if (await InvokeHandlersAsync(context, ex, fallback))
            {
                return;
            }
            await WriteErrorAsync(context, fallback, 500);
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

    /// <summary>消费 IMeshExceptionHandler 扩展点(SSS+三轮审核:此前全仓零消费=死扩展点)。
    /// 业务注册的处理器按 Priority 升序执行;任一 IsTerminating=true 即视为已接管响应。</summary>
    private async Task<bool> InvokeHandlersAsync(HttpContext context, Exception ex, ApiResult error)
    {
        var handlers = context.RequestServices.GetServices<IMeshExceptionHandler>()
            .OrderBy(h => h.Priority)
            .ToArray();
        if (handlers.Length == 0)
        {
            return false;
        }
        var ctx = new ExceptionContext(context, ex, error);
        var terminating = false;
        foreach (var handler in handlers)
        {
            if (!handler.CanHandle(ex))
            {
                continue;
            }
            try
            {
                await handler.HandleAsync(ctx);
                terminating |= handler.IsTerminating;
            }
            catch (Exception handlerEx)
            {
                // 处理器自身异常不掩盖原始异常,仅记录
                _logger.LogError(handlerEx, "IMeshExceptionHandler {Handler} 自身异常", handler.GetType().Name);
            }
        }
        return terminating;
    }

    /// <summary>异常 → 错误信封:Code=细粒度业务码 ErrorCode(与错误码参考文档对齐;
    /// 未显式给码时 ErrorCode=(int)StatusCode,401/404/500 语义不变);Validation 附 Errors。</summary>
    internal static ApiResult BuildError(MeshException ex, string traceId)
    {
        var message = ex.ShouldExposeMessage
            ? ex.Message
            : $"服务器内部错误(TraceId:{traceId})";
        var error = ApiResult.Fail(message, ex.ErrorCode);
        error.TraceId = traceId;
        if (ex is ValidationException { Errors.Count: > 0 } validation)
        {
            error.Errors = validation.Errors;
        }
        return error;
    }

    private static async Task WriteErrorAsync(HttpContext context, ApiResult error, int statusCode)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode; // HTTP 状态=ex.StatusCode;信封 Code=细粒度 ErrorCode(两者分离)
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(error, JsonSerializerOptions.Web));
    }
}
