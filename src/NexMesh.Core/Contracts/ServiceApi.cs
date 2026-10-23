namespace NexMesh;

/// <summary>
/// 服务 API 契约标记接口:实现此接口的契约经 AddNexMeshContracts 自动注册 DI、
/// MapNexMeshContracts 自动映射 REST 端点——接口即路由,业务只写契约与实现。
/// </summary>
public interface IServiceApi
{
}

/// <summary>
/// 声明服务契约:Name 缺省从接口名推导(去前导 I、去尾部 Api → 服务名,即路由前缀 api/{服务名})。
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class ApiContractAttribute : Attribute
{
    /// <summary>显式服务名(路由前缀用);null = 按接口名推导。</summary>
    public string? Name { get; }

    public ApiContractAttribute()
    {
    }

    public ApiContractAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>表单声明:复杂参数从 multipart/urlencoded 表单字段绑定(简单属性同名映射,IFormFile 属性按名匹配文件)。</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromFormAttribute : Attribute
{
}
