namespace NexMesh.Core.Tests;

using System.Text;
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
        builder.Configuration["NexMesh:AutoApi:AllowAnonymousByDefault"] = "true"; // 旧用例回退匿名;默认拒绝由新用例单独验证
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<CrudApiService>();
        builder.Services.AddScoped<DemoApiService>();

        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        // 网关模式:不注册任何字面量端点,全部经通配物理路由分发
        _app.MapAutoApiGateway(typeof(CrudApiService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task 字面量逻辑路由_经网关分发()
    {
        var response = await _client.PostAsync("/api/demo/getlistasync", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("[1,2,3]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 模板逻辑路由_捕获路径参数()
    {
        var response = await _client.GetAsync("/api/crud/42");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("\"item-42\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 模板路由_混合绑定路径参数加Body()
    {
        var response = await _client.PutAsync("/api/crud/7",
            new StringContent("""{"name":"X","qty":1}""", Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("\"item-7:X\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 未注册路径_返回404业务码()
    {
        var response = await _client.GetAsync("/api/nowhere/42");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("40400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 动词不符_返回405()
    {
        // /api/crud/{id} 注册为 GET;用 POST 访问 → 405
        var response = await _client.PostAsync("/api/crud/42", content: null);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("40500", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query绑定_网关模式下同样生效()
    {
        var response = await _client.PostAsync("/api/demo/getasync?id=9", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("9", await response.Content.ReadAsStringAsync());
    }
}
