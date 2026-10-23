namespace NexMesh.Core.Models;

/// <summary>统一返回结构。Code 透传业务错误码,Succeeded 按 2xx 区间判定。</summary>
public class ApiResult<T>
{
    public int Code { get; set; } = 200;
    public string Message { get; set; } = "Success";
    public T? Data { get; set; }
    public bool Succeeded => Code is >= 200 and < 300;

    public static ApiResult<T> Ok(T data, string msg = "Success") => new() { Code = 200, Message = msg, Data = data };
    public static ApiResult<T> Fail(int code, string msg) => new() { Code = code, Message = msg, Data = default };
}

/// <summary>统一分页响应。</summary>
public class PagedData<T>
{
    public long Total { get; set; }
    public List<T> Items { get; set; } = [];
}

/// <summary>统一分页请求。</summary>
public class PagedRequest
{
    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? KeyWords { get; set; }
    public string? OrderBy { get; set; }
}
