namespace NexMesh.Core.Tests.Fixtures;

using Microsoft.AspNetCore.Mvc;
using NexMesh;

/// <summary>契约端点测试夹具:覆盖无模板方法名路由/简单参数 Query 绑定/同步返回/非泛型 Task 四种形态。</summary>
public interface IDemoApi : IServiceApi
{
    Task<List<int>> GetListAsync();

    // 简单参数:默认 POST 子路由(api/Demo/Get)+ Query 绑定
    Task<int> GetAsync(int id);

    string Ping();

    // 非泛型 Task:空载荷成功信封(200 + code=0)
    Task ResetAsync();
}

public class DemoApiService : IDemoApi
{
    public Task<List<int>> GetListAsync() => Task.FromResult(new List<int> { 1, 2, 3 });

    public Task<int> GetAsync(int id) => Task.FromResult(id);

    public string Ping() => "pong";

    public Task ResetAsync() => Task.CompletedTask;
}

/// <summary>契约参数绑定夹具:路由参数/Body/混合绑定/分页/动词特性。</summary>
public interface ICrudApi : IServiceApi
{
    record ItemDto(string Name, int Qty);

    [HttpGet("{id}")]
    Task<string> GetAsync(long id);

    [HttpPost]
    Task<ItemDto> CreateAsync(ItemDto dto);

    [HttpPut("{id}")]
    Task<string> UpdateAsync(long id, ItemDto dto);

    [HttpDelete("{id}")]
    Task<bool> DeleteAsync(long id);

    // 默认 POST + Query 绑定 + 可缺省参数默认值
    Task<PagedResult<string>> SearchAsync(string? keyword, int pageIndex = 1, int pageSize = 20);
}

public class CrudApiService : ICrudApi
{
    public Task<string> GetAsync(long id) => Task.FromResult($"item-{id}");

    public Task<ICrudApi.ItemDto> CreateAsync(ICrudApi.ItemDto dto) => Task.FromResult(dto);

    public Task<string> UpdateAsync(long id, ICrudApi.ItemDto dto) => Task.FromResult($"item-{id}:{dto.Name}");

    public Task<bool> DeleteAsync(long id) => Task.FromResult(true);

    public Task<PagedResult<string>> SearchAsync(string? keyword, int pageIndex = 1, int pageSize = 20)
        => Task.FromResult(new PagedResult<string>
        {
            TotalCount = pageIndex,
            PageIndex = pageIndex,
            PageSize = pageSize,
            Items = [keyword ?? "all"],
        });
}

/// <summary>异常中间件测试夹具:业务异常 vs 未知异常。</summary>
public interface IThrowApi : IServiceApi
{
    Task BizAsync();

    Task BoomAsync();
}

public class ThrowingApiService : IThrowApi
{
    public Task BizAsync() => throw new MeshException("金额必须大于 0", 40002, 400);

    public Task BoomAsync() => throw new InvalidOperationException("boom");
}

/// <summary>异常族语义夹具:BusinessException 暴露消息 / InfrastructureException 不暴露。</summary>
public interface IExceptionFamilyApi : IServiceApi
{
    [HttpPost("business")]
    Task BizFamilyAsync();

    [HttpPost("not-found")]
    Task MissingAsync();

    [HttpPost("infra")]
    Task InfraAsync();

    [HttpPost("validation")]
    Task InvalidAsync();
}

public class ExceptionFamilyService : IExceptionFamilyApi
{
    public Task BizFamilyAsync() => throw new BusinessException("账号不能为空");

    public Task MissingAsync() => throw new NotFoundException("工单 42 不存在");

    public Task InfraAsync() => throw new InfrastructureException("数据库连接串泄漏风险细节");

    public Task InvalidAsync() => throw new ValidationException("字段校验失败", new Dictionary<string, string[]>
    {
        ["userName"] = ["必填", "长度 2-32"],
    });
}

/// <summary>接口名不带 Api 后缀的夹具:IPlainApi → 服务名 Plain → api/Plain/Count。</summary>
public interface IPlainApi : IServiceApi
{
    Task<int> CountAsync();
}

public class PlainApiService : IPlainApi
{
    public Task<int> CountAsync() => Task.FromResult(7);
}

/// <summary>路由模板含参数约束的夹具(业务契约的标准形态)。</summary>
public interface IWidgetBiz : IServiceApi
{
    [HttpGet("{code}")]
    Task<string> FindAsync(string code);
}

public class WidgetService : IWidgetBiz
{
    public Task<string> FindAsync(string code) => Task.FromResult($"widget-{code}");
}
