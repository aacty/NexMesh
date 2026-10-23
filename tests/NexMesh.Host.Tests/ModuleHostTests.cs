namespace NexMesh.Host.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Auth;

/// <summary>模块生命周期验证:AppModule 自动发现 + 两阶段执行 + Security 装配 + 契约端点。</summary>
public class ModuleHostTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public static readonly List<string> Lifecycle = [];

    /// <summary>业务模块(宿主闭包扫描自动发现):Phase=Application。</summary>
    public class DemoModule : AppModule
    {
        public override string Name => "DemoModule";

        public override void ConfigureServices(NexMeshBuilder builder)
        {
            Lifecycle.Add("ConfigureServices");
            builder.Services.AddSingleton<IModuleProbe, ModuleProbe>();
        }

        public override void ConfigureApplication(WebApplication app)
        {
            Lifecycle.Add("ConfigureApplication");
        }
    }

    public interface IModuleProbe
    {
        string Probe();
    }

    public class ModuleProbe : IModuleProbe
    {
        public string Probe() => "module-service-ok";
    }

    public interface IModuleApi : IServiceApi
    {
        Task<string> ModuleProbeAsync();
    }

    public class ModuleApiService(IModuleProbe probe) : IModuleApi
    {
        public Task<string> ModuleProbeAsync() => Task.FromResult(probe.Probe());
    }

    public class TestUserInfo
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string> Roles { get; set; } = [];
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.AddNexMeshHost([typeof(ModuleHostTests).Assembly], mb =>
        {
            mb.AddSecurity(security =>
            {
                security.AddCurrentUser<TestUserInfo>();
            });
        });

        _app = builder.Build();
        _app.UseNexMeshHost();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task 模块自动发现_两阶段按序执行_服务注册生效()
    {
        // ConfigureServices 在 AddNexMeshHost 阶段执行
        Assert.Contains("ConfigureServices", Lifecycle);
        // ConfigureApplication 在 UseNexMeshHost 阶段执行
        Assert.Contains("ConfigureApplication", Lifecycle);

        // 模块注册的服务经 DI 注入契约端点
        var response = await _client.PostAsync("/api/Module/ModuleProbe", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("module-service-ok", body);
    }

    [Fact]
    public void Security装配_当前用户与令牌服务可用()
    {
        var currentUser = _app.Services.GetRequiredService<ICurrentUser<TestUserInfo>>();
        Assert.NotNull(currentUser);
        var jwt = _app.Services.GetRequiredService<NexMesh.Auth.IJwtTokenService>();
        Assert.NotNull(jwt);
    }

    [Fact]
    public async Task 宿主端点_健康检查可用()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
