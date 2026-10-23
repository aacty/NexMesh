namespace NexMesh.Core.Tests.Fixtures;

using Microsoft.AspNetCore.Mvc;
using NexMesh.Core;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Models;

/// <summary>AutoApi 测试夹具:覆盖无参映射/简单参数 Query 绑定/同步返回/非泛型 Task 四种形态。</summary>
[ServiceRoute("api/demo")]
public class DemoApiService : IAutoApi
{
    public Task<List<int>> GetListAsync() => Task.FromResult(new List<int> { 1, 2, 3 });

    // 简单参数:默认 POST 子路由 + Query 绑定
    public Task<int> GetAsync(int id) => Task.FromResult(id);

    public string Ping() => "pong";

    // 非泛型 Task:无返回值 → 204
    public Task ResetAsync() => Task.CompletedTask;
}

/// <summary>AutoApi 参数绑定测试夹具:路由参数/Body/混合绑定/分页 Query/动词特性。</summary>
[ServiceRoute("api/crud")]
public class CrudApiService : IAutoApi
{
    public record ItemDto(string Name, int Qty);

    [HttpGet("{id}")]
    public Task<string> GetAsync(long id) => Task.FromResult($"item-{id}");

    [HttpPost]
    public Task<ItemDto> CreateAsync(ItemDto dto) => Task.FromResult(dto);

    [HttpPut("{id}")]
    public Task<string> UpdateAsync(long id, ItemDto dto) => Task.FromResult($"item-{id}:{dto.Name}");

    [HttpDelete("{id}")]
    public Task<bool> DeleteAsync(long id) => Task.FromResult(true);

    // 默认 POST + Query 绑定 + 可缺省参数默认值
    public Task<PagedData<string>> SearchAsync(string? keyWords, int pageIndex = 1, int pageSize = 20)
        => Task.FromResult(new PagedData<string> { Total = pageIndex, Items = [keyWords ?? "all"] });
}

/// <summary>异常中间件测试夹具:业务异常 vs 未知异常。</summary>
[ServiceRoute("api/throw")]
public class ThrowingApiService : IAutoApi
{
    public Task BizAsync() => throw new MeshException("金额必须大于 0", 40002, 400);

    public Task BoomAsync() => throw new InvalidOperationException("boom");
}

/// <summary>未标注 [ServiceRoute] 的夹具:验证 api/{类名小写} 默认路由规则。</summary>
public class PlainApiService : IAutoApi
{
    public Task<int> CountAsync() => Task.FromResult(7);
}

/// <summary>接口声明 [Http*] 特性的夹具:特性应沿 InterfaceMap 上溯生效(业务契约的标准形态)。</summary>
public interface IWidgetBiz : IAutoApi
{
    [HttpGet("{code}")]
    Task<string> FindAsync(string code);
}

public class WidgetService : IWidgetBiz
{
    public Task<string> FindAsync(string code) => Task.FromResult($"widget-{code}");
}

