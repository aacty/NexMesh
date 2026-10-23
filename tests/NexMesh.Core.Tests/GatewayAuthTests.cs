using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Tests.Fixtures;

namespace NexMesh.Core.Tests;

/// <summary>SSS+第二轮:网关默认拒绝(与字面量模式同授权语义)。</summary>
public class GatewayAuthTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMeshContracts(typeof(CrudApiService).Assembly);
        // 不设 AllowAnonymousByDefault → 默认拒绝
        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        _app.MapNexMeshContractGateway(typeof(CrudApiService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task 未标注AllowAnonymous的端点_匿名访问401()
    {
        var response = await _client.PostAsync("/api/Demo/GetList", content: null);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":401", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 未注册路由_仍然404而非401()
    {
        // 授权检查只发生在路由命中之后;未注册路径优先 404
        var response = await _client.GetAsync("/api/nowhere/42");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
