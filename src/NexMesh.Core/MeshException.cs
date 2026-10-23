namespace NexMesh.Core;

/// <summary>业务异常:ErrorCode 透传到 ApiResult.Code,StatusCode 控制 HTTP 状态码(默认 400/40000)。</summary>
public class MeshException(string message, int errorCode = 40000, int statusCode = 400)
    : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
    public int StatusCode { get; } = statusCode;
}
