namespace NexMesh.Core.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using NexMesh.Core.Tests.Fixtures;

/// <summary>TestServer 装配(遵循仓库测试约定:TestHost 直连,不用 WebApplicationFactory)。</summary>
public sealed class TestApi : IAsyncLifetime
{
    private WebApplication _app = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // SSS+安全:契约端点默认拒绝;测试容器回退为默认匿名(授权场景由独立测试覆盖)
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMeshContracts(typeof(DemoApiService).Assembly);

        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        _app.MapNexMeshContracts(typeof(DemoApiService).Assembly);
        // 响应已开始后抛异常(SSE 流式中断场景):不得尝试改写响应头造成二次崩溃
        _app.MapGet("/stream-then-throw", async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.Body.WriteAsync("partial"u8.ToArray());
            await ctx.Response.Body.FlushAsync();
            throw new InvalidOperationException("mid-stream boom");
        });
        await _app.StartAsync();

        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
