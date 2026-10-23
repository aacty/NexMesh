namespace NexMesh.Core.Middlewares;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NexMesh.Core.Models;

/// <summary>
/// 全局异常中间件:X-Trace-Id 响应头 + MeshException 业务码/状态码透传,未知异常统一 500;
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
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = ex.StatusCode;

            var result = ApiResult<object>.Fail(ex.ErrorCode, ex.Message);
            await context.Response.WriteAsync(JsonSerializer.Serialize(result));
        }
        catch (Exception ex) when (!context.Response.HasStarted
                                   && ex is not OperationCanceledException)
        {
            // SSS+安全审查修复:500 不向客户端暴露 ex.Message(可含 SQL/连接串细节);
            // 统一 50000 业务码;详细异常仅入日志(带 TraceId)
            _logger.LogError(ex, "未处理异常 {TraceId} {Method} {Path}",
                traceId, context.Request.Method, context.Request.Path);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var result = ApiResult<object>.Fail(50000, $"服务器内部错误(TraceId:{traceId})");
            await context.Response.WriteAsync(JsonSerializer.Serialize(result));
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
}
