namespace NexMesh;

/// <summary>统一响应基类:Success 判定 + Code(成功=0/失败=HTTP 状态码)+ TraceId/Timestamp/Errors。</summary>
public abstract class ApiResultBase
{
    /// <summary>是否成功(前端首要判定字段)。</summary>
    public bool Success { get; set; }

    /// <summary>业务码:成功恒为 0;失败为 HTTP 状态码(400/401/404/500...)。</summary>
    public int Code { get; set; }

    public string Message { get; set; } = "";

    /// <summary>链路追踪标识(与 X-Trace-Id 响应头一致)。</summary>
    public string? TraceId { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>字段级校验错误(ValidationException 专用),其他场景为 null。</summary>
    public IDictionary<string, string[]>? Errors { get; set; }
}

/// <summary>无数据载荷的统一响应。</summary>
public class ApiResult : ApiResultBase
{
    /// <summary>成功工厂:Code=0,Message 可自定义。</summary>
    public static ApiResult Ok(string message = "") =>
        new() { Success = true, Code = 0, Message = message };

    /// <summary>失败工厂:Code 默认 400(可传 HTTP 状态码)。</summary>
    public static ApiResult Fail(string message, int code = 400) =>
        new() { Success = false, Code = code, Message = message };
}

/// <summary>带数据载荷的统一响应。</summary>
public class ApiResult<T> : ApiResult
{
    public T? Data { get; set; }

    public static ApiResult<T> Ok(T? data, string message = "") =>
        new() { Success = true, Code = 0, Message = message, Data = data };

    public static new ApiResult<T> Fail(string message, int code = 400) =>
        new() { Success = false, Code = code, Message = message };
}
