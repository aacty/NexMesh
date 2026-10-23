namespace NexMesh.Core.Tests;

using System.Text;
using System.Text.Json;
using NexMesh.Core.Tests.Fixtures;

public class BindingTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task 路由参数_HttpGet特性模板绑定()
    {
        var response = await api.Client.GetAsync("/api/crud/42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("\"item-42\"", body);
    }

    [Fact]
    public async Task 简单参数_默认POST路由从Query绑定()
    {
        var response = await api.Client.PostAsync("/api/demo/getasync?id=5", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("5", body);
    }

    [Fact]
    public async Task 复杂参数_从JSONBody绑定()
    {
        var response = await api.Client.PostAsync("/api/crud",
            new StringContent("""{"name":"Widget","qty":3}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("Widget", body);
        Assert.Contains("3", body);
    }

    [Fact]
    public async Task 混合绑定_路由参数加Body()
    {
        var response = await api.Client.PutAsync("/api/crud/7",
            new StringContent("""{"name":"X","qty":1}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("\"item-7:X\"", body);
    }

    [Fact]
    public async Task DELETE动词_路由参数()
    {
        var response = await api.Client.DeleteAsync("/api/crud/9");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 分页Query_缺省参数取默认值()
    {
        var response = await api.Client.PostAsync("/api/crud/searchasync?keyWords=abc&pageIndex=3", content: null);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal("abc", json.GetProperty("items")[0].GetString());

        // 全缺省:keyWords → null → "all",pageIndex → 1
        var response2 = await api.Client.PostAsync("/api/crud/searchasync", content: null);
        var json2 = JsonSerializer.Deserialize<JsonElement>(await response2.Content.ReadAsStringAsync());
        Assert.Equal(1, json2.GetProperty("total").GetInt32());
        Assert.Equal("all", json2.GetProperty("items")[0].GetString());
    }

    [Fact]
    public async Task 非法JSONBody_返回400业务码()
    {
        var response = await api.Client.PostAsync("/api/crud",
            new StringContent("{bad json", Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(40001, json.GetProperty("Code").GetInt32());
    }

    [Fact]
    public async Task 接口声明的Http特性_沿InterfaceMap上溯生效()
    {
        // 业务契约标准形态:[HttpGet("{code}")] 标注在接口方法上,实现类不重复标注
        var response = await api.Client.GetAsync("/api/widgetservice/abc");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("\"widget-abc\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 参数格式错误_返回400()
    {
        var response = await api.Client.PostAsync("/api/demo/getasync?id=notanumber", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
