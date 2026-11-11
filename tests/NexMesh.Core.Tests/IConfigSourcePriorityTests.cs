namespace NexMesh.Core.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.Configuration;

/// <summary>
/// 配置源优先级攻击面(交叉测试 I):
/// 文档《配置参考.md》承诺优先级从低到高:Configs/*.json → 环境变量 → 命令行。
/// 简装入口 AddNexMeshService 将 AddNexMeshConfig 追加在 WebApplicationBuilder 默认源
/// (含环境变量与命令行)之后,Configs 会反向覆盖环境变量/命令行——本组测试按文档语义断言。
/// </summary>
public class IConfigSourcePriorityTests : IDisposable
{
    private readonly string _dir;

    public IConfigSourcePriorityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexmesh-ipri-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void 环境变量_应覆盖Configs文件同名键_按文档优先级()
    {
        File.WriteAllText(Path.Combine(_dir, "pri.json"),
            """{"IPri":{"V":"from-file"}}""");
        Environment.SetEnvironmentVariable("IPRI__V", "from-env");
        try
        {
            // 复现简装入口的真实顺序:WebApplicationBuilder 默认源(此处以环境变量源代表)在前,
            // AddNexMeshConfig 追加在后
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddNexMeshConfig(_dir, "Production")
                .Build();

            Assert.Equal("from-env", config["IPri:V"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPRI__V", null);
        }
    }

    [Fact]
    public void 真实宿主环境_环境变量覆盖Configs文件()
    {
        File.WriteAllText(Path.Combine(_dir, "pri.json"),
            """{"IPri":{"Host":"from-file"}}""");
        Environment.SetEnvironmentVariable("IPRI__HOST", "from-env");
        try
        {
            // WebApplication.CreateBuilder 默认源已含环境变量,与 AddNexMeshService 同序追加 Configs
            var builder = WebApplication.CreateBuilder();
            builder.Configuration.AddNexMeshConfig(_dir, "Production");

            Assert.Equal("from-env", builder.Configuration["IPri:Host"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPRI__HOST", null);
        }
    }

    [Fact]
    public void 同名键_跨基础文件_按文件名排序加载_后者确定性覆盖()
    {
        File.WriteAllText(Path.Combine(_dir, "aa.json"), """{"Dup":{"K":"from-aa"}}""");
        File.WriteAllText(Path.Combine(_dir, "zz.json"), """{"Dup":{"K":"from-zz"}}""");

        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();

        // 文件枚举顺序未被契约保证:必须显式排序,保证"字母序后者赢"跨平台确定
        Assert.Equal("from-zz", config["Dup:K"]);
    }

    [Fact]
    public void 嵌套占位符_原样保留_不静默产出错误值()
    {
        File.WriteAllText(Path.Combine(_dir, "nest.json"),
            """{"Nest":{"V":"${A:${B}}"}}""");

        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();

        // 嵌套占位符语义未定义,但不应被正则截断成垃圾值("}")
        Assert.Equal("${A:${B}}", config["Nest:V"]);
    }

    [Fact]
    public void 占位符_变量缺失且无默认值_展开为空串()
    {
        File.WriteAllText(Path.Combine(_dir, "missing.json"),
            """{"N":{"V":"${IPRI_DEFINITELY_MISSING_VAR}"}}""");

        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();

        Assert.Equal("", config["N:V"]);
    }

    [Fact]
    public void 占位符_环境变量存在但为空串_取环境变量空串()
    {
        File.WriteAllText(Path.Combine(_dir, "empty.json"),
            """{"N":{"V":"${IPRI_EMPTY_VAR|fallback}"}}""");
        Environment.SetEnvironmentVariable("IPRI_EMPTY_VAR", "");
        try
        {
            var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();

            Assert.Equal("", config["N:V"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPRI_EMPTY_VAR", null);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>热更攻击面:reloadOnChange 链路(文件 → 内嵌源 → 占位符 provider → 外层 Root → Options)。</summary>
public class IConfigHotReloadTests : IDisposable
{
    private sealed class HotOpts
    {
        public string V { get; set; } = "";
    }

    private readonly string _dir;

    public IConfigHotReloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexmesh-ihot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task 热更_文件修改后_配置读取到新值()
    {
        var file = Path.Combine(_dir, "hot.json");
        File.WriteAllText(file, """{"Hot":{"V":"v1"}}""");
        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();
        try
        {
            Assert.Equal("v1", config["Hot:V"]);

            File.WriteAllText(file, """{"Hot":{"V":"v2"}}""");

            await WaitUntilAsync(() => config["Hot:V"] == "v2");
            Assert.Equal("v2", config["Hot:V"]);
        }
        finally
        {
            (config as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task 热更后_IOptionsMonitor取新值_而IOptions保持启动快照()
    {
        var file = Path.Combine(_dir, "hot2.json");
        File.WriteAllText(file, """{"Hot":{"V":"v1"}}""");
        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, "Production").Build();
        try
        {

            var services = new ServiceCollection();
            services.AddOptions<HotOpts>().Bind(config.GetSection("Hot"));
            using var provider = services.BuildServiceProvider();
            var monitor = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HotOpts>>();
            var snapshot = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HotOpts>>();
            Assert.Equal("v1", monitor.CurrentValue.V);
            Assert.Equal("v1", snapshot.Value.V);

            File.WriteAllText(file, """{"Hot":{"V":"v2"}}""");

            await WaitUntilAsync(() => monitor.CurrentValue.V == "v2");
            Assert.Equal("v2", monitor.CurrentValue.V);
            Assert.Equal("v1", snapshot.Value.V); // IOptions 语义:启动快照,不随热更变化
        }
        finally
        {
            (config as IDisposable)?.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = TimeSpan.FromSeconds(20);
        var start = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - start < deadline)
        {
            await Task.Delay(200);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
