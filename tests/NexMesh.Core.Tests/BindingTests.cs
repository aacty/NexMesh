namespace NexMesh.Core.Tests;

using System.Text;
using System.Text.Json;
using NexMesh.Core.Tests.Fixtures;

/// <summary>契约绑定边界:动词/非法 JSON/参数格式错误/缺省参数默认值(基础绑定由 ContractApiTests 与 GatewayTests 覆盖)。</summary>
public class BindingTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task DELETE动词_路由参数()
    {
        var response = await api.Client.DeleteAsync("/api/Crud/9");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.True(JsonSerializer.Deserialize<JsonElement>(body).GetProperty("data").GetBoolean());
    }

    [Fact]
    public async Task 分页Query_缺省参数取默认值()
    {
        // 缺省参数:pageIndex 显式传 3,keyWords 提供
        var response = await api.Client.PostAsync("/api/Crud/Search?keyword=abc&pageIndex=3", content: null);
        var page = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("data");
        Assert.Equal(3, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("abc", page.GetProperty("items")[0].GetString());

        // 全缺省:keyword → null → "all",pageIndex → 1
        var response2 = await api.Client.PostAsync("/api/Crud/Search", content: null);
        var page2 = JsonSerializer.Deserialize<JsonElement>(await response2.Content.ReadAsStringAsync()).GetProperty("data");
        Assert.Equal(1, page2.GetProperty("pageIndex").GetInt32());
        Assert.Equal("all", page2.GetProperty("items")[0].GetString());
    }

    [Fact]
    public async Task 非法JSONBody_返回400()
    {
        var response = await api.Client.PostAsync("/api/Crud/Create",
            new StringContent("{bad json", Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.GetProperty("code").GetInt32());
        Assert.Contains("JSON", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 参数格式错误_返回400()
    {
        var response = await api.Client.PostAsync("/api/Demo/Get?id=notanumber", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.GetProperty("code").GetInt32());
    }
}
