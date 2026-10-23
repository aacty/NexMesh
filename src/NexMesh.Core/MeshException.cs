namespace NexMesh;

using System.Net;

/// <summary>
/// 框架异常基类:StatusCode 控制 HTTP 状态码,ErrorCode 保留细粒度业务码(日志诊断用)。
/// 异常族(BusinessException 等)按类型自带状态码与消息暴露策略,业务按语义抛出即可。
/// </summary>
public class MeshException : Exception
{
    /// <summary>细粒度业务码(诊断用;默认取 HTTP 状态码数值)。</summary>
    public int ErrorCode { get; }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    public HttpStatusCode HttpStatusCode => (HttpStatusCode)StatusCode;

    /// <summary>消息是否可暴露给客户端(InfrastructureException 等内部异常为 false)。</summary>
    public virtual bool ShouldExposeMessage => true;

    /// <summary>框架内部用:自定义业务码 + 状态码。</summary>
    public MeshException(string message, int errorCode = 40000, int statusCode = 400) : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    /// <summary>HTTP 状态码形式(异常族基类构造)。</summary>
    public MeshException(string message, HttpStatusCode statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = (int)statusCode;
        StatusCode = (int)statusCode;
    }
}

/// <summary>业务规则不满足(400,消息可暴露)。</summary>
public class BusinessException : MeshException
{
    public BusinessException(string message) : base(message, HttpStatusCode.BadRequest)
    {
    }

    public BusinessException(string message, HttpStatusCode statusCode) : base(message, statusCode)
    {
    }
}

/// <summary>基础设施故障(500,消息不暴露——可能含连接串/SQL 细节,响应统一为通用文案)。</summary>
public class InfrastructureException : MeshException
{
    public override bool ShouldExposeMessage => false;

    public InfrastructureException(string message) : base(message, HttpStatusCode.InternalServerError)
    {
    }

    public InfrastructureException(string message, Exception innerException)
        : base(message, HttpStatusCode.InternalServerError, innerException)
    {
    }
}

/// <summary>资源不存在(404)。</summary>
public class NotFoundException : MeshException
{
    public NotFoundException(string message) : base(message, HttpStatusCode.NotFound)
    {
    }
}

/// <summary>未认证/凭证无效(401)。</summary>
public class UnauthorizedException : MeshException
{
    public UnauthorizedException(string message) : base(message, HttpStatusCode.Unauthorized)
    {
    }
}

/// <summary>请求格式错误(400)。</summary>
public class BadRequestException : MeshException
{
    public BadRequestException(string message) : base(message, HttpStatusCode.BadRequest)
    {
    }
}

/// <summary>请求被拒绝(403)。</summary>
public class ForbiddenException : MeshException
{
    public ForbiddenException(string message) : base(message, HttpStatusCode.Forbidden)
    {
    }
}

/// <summary>实体重复/冲突(409)。</summary>
public class DuplicateEntityException : MeshException
{
    public DuplicateEntityException(string message) : base(message, HttpStatusCode.Conflict)
    {
    }
}

/// <summary>字段级校验失败(400 + Errors 字典)。</summary>
public class ValidationException : MeshException
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string message) : base(message, HttpStatusCode.BadRequest)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string message, IDictionary<string, string[]> errors)
        : base(message, HttpStatusCode.BadRequest)
    {
        Errors = errors;
    }
}

/// <summary>下游远程服务返回错误(502)。</summary>
public class RemoteServiceException : MeshException
{
    /// <summary>下游服务名(诊断用)。</summary>
    public string ServiceName { get; }

    public RemoteServiceException(string serviceName, string message,
        HttpStatusCode statusCode = HttpStatusCode.BadGateway)
        : base(message, statusCode)
    {
        ServiceName = serviceName;
    }
}

/// <summary>下游远程服务不可用(503)。</summary>
public class ServiceUnavailableException : MeshException
{
    public string ServiceName { get; }

    public ServiceUnavailableException(string serviceName, string message)
        : base(message, HttpStatusCode.ServiceUnavailable)
    {
        ServiceName = serviceName;
    }
}

/// <summary>下游远程服务超时(504)。</summary>
public class ServiceTimeoutException : MeshException
{
    public string ServiceName { get; }

    public ServiceTimeoutException(string serviceName, string message)
        : base(message, HttpStatusCode.GatewayTimeout)
    {
        ServiceName = serviceName;
    }
}

/// <summary>
/// 全局异常处理扩展点:业务可实现并注册(IMeshExceptionHandler),在框架写出错误响应前
/// 参与处理(自定义日志/告警)。CanHandle 返回 true 且 IsTerminating 时接管响应写出。
/// </summary>
public interface IMeshExceptionHandler
{
    /// <summary>执行顺序(小者先)。</summary>
    int Priority { get; }

    /// <summary>true = 处理后终止管线(框架不再写默认错误响应)。默认实现返回 false。</summary>
    bool IsTerminating => false;

    bool CanHandle(Exception exception);

    Task HandleAsync(ExceptionContext context);
}

/// <summary>异常处理上下文:请求 + 原始异常 + 框架即将写出的错误响应。</summary>
public sealed record ExceptionContext(
    Microsoft.AspNetCore.Http.HttpContext HttpContext,
    Exception Exception,
    ApiResult Error);
