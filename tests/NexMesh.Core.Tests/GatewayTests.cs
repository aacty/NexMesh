namespace NexMesh.Core.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Tests.Fixtures;

public class GatewayTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true"; // 旧用例回退匿名;默认拒绝由 GatewayAuthTests 单独验证
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMeshContracts(typeof(CrudApiService).Assembly);

        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        // 网关模式:不注册任何字面量端点,全部经通配物理路由分发
        _app.MapNexMeshContractGateway(typeof(CrudApiService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<JsonElement> DataOf(System.Net.Http.HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("data");
    }

    [Fact]
    public async Task 字面量逻辑路由_经网关分发()
    {
        var response = await _client.PostAsync("/api/Demo/GetList", content: null);

        Assert.Equal([1, 2, 3], (await DataOf(response)).Deserialize<List<int>>());
    }

    [Fact]
    public async Task 模板逻辑路由_捕获路径参数()
    {
        var response = await _client.GetAsync("/api/Crud/42");

        Assert.Equal("item-42", (await DataOf(response)).GetString());
    }

    [Fact]
    public async Task 模板路由_混合绑定路径参数加Body()
    {
        var response = await _client.PutAsync("/api/Crud/7",
            new StringContent("""{"name":"X","qty":1}""", Encoding.UTF8, "application/json"));

        Assert.Equal("item-7:X", (await DataOf(response)).GetString());
    }

    [Fact]
    public async Task 未注册路径_返回404业务码()
    {
        var response = await _client.GetAsync("/api/nowhere/42");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":404", body);
    }

    [Fact]
    public async Task 动词不符_返回405()
    {
        // /api/Crud/{id} 注册为 GET;用 POST 访问 → 405
        var response = await _client.PostAsync("/api/Crud/42", content: null);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("\"code\":405", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query绑定_网关模式下同样生效()
    {
        var response = await _client.PostAsync("/api/Demo/Get?id=9", content: null);

        Assert.Equal(9, (await DataOf(response)).GetInt32());
    }
}
