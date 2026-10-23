namespace NexMesh.Host.Tests;

using FreeSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI.Core;
using NexMesh.AI.Tools;
using NexMesh.Host;

/// <summary>
/// 宿主总装端到端:一行 AddNexMeshService/UseNexMeshService 后,
/// 约定注入 / AutoApi / FreeSql(内存 SQLite) / AI 工具注册 全部就位。
/// </summary>
public class HostEndToEndTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

        // 每个测试类实例独立临时 SQLite 文件(确定性,无跨实例状态)
        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
            builder.WebHost.UseTestServer();
            // 临时文件 SQLite + 无 AI Provider 配置 → 验证"零配置也能完整跑起来"
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexMesh:Db:ConnectionString"] = $"Data Source=host-{Guid.NewGuid():N}.db",
                ["NexMesh:Db:AutoSyncStructure"] = "true",
            });

        builder.AddNexMeshService(typeof(HostDemoService).Assembly);
        _app = builder.Build();
        _app.UseNexMeshService(typeof(HostDemoService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task 一行总装后_契约端点立即可用()
    {
        var response = await _client.PostAsync("/api/HostDemo/Ping", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"host-ok\"", body); // 信封 data 内
    }

    [Fact]
    public async Task 一行总装后_IFreeSql已注入且可写读()
    {
        var response = await _client.PostAsync("/api/HostDemo/Count", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"data\":1", body);
    }

    [Fact]
    public void 一行总装后_AI工厂与工具注册就位()
    {
        var factory = _app.Services.GetRequiredService<INexMeshChatFactory>();
        var tools = _app.Services.GetRequiredService<INexMeshToolRegistry>().DiscoverTools().ToList();

        Assert.NotNull(factory);
        Assert.Contains(tools, t => t.Name.Contains("Probe", StringComparison.OrdinalIgnoreCase));
    }
}
