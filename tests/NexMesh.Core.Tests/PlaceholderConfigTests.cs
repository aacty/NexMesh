namespace NexMesh.Core.Tests;

using Microsoft.Extensions.Configuration;
using NexMesh.Core.Configuration;

public class PlaceholderConfigTests : IDisposable
{
    private readonly string _dir;

    public PlaceholderConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexmesh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXMESH_TEST_URL", "postgres://from-env");
    }

    [Fact]
    public void 环境变量占位符_展开为环境变量值()
    {
        File.WriteAllText(Path.Combine(_dir, "db.json"),
            """{"Db":{"Url":"${NEXMESH_TEST_URL|postgres://default}"}}""");
        var config = BuildConfig("Production");

        Assert.Equal("postgres://from-env", config["Db:Url"]);
    }

    [Fact]
    public void 环境变量缺失时_取竖线后的默认值()
    {
        File.WriteAllText(Path.Combine(_dir, "db.json"),
            """{"Db":{"Url":"${NEXMESH_TEST_MISSING|postgres://default}"}}""");
        var config = BuildConfig("Production");

        Assert.Equal("postgres://default", config["Db:Url"]);
    }

    [Fact]
    public void 环境变体文件_后加载并覆盖基础值()
    {
        File.WriteAllText(Path.Combine(_dir, "db.json"),
            """{"Db":{"Url":"base","Key":"base-key"}}""");
        File.WriteAllText(Path.Combine(_dir, "db.dev.json"),
            """{"Db":{"Key":"dev-override"}}""");
        var config = BuildConfig("dev");

        Assert.Equal("base", config["Db:Url"]);
        Assert.Equal("dev-override", config["Db:Key"]);
    }

    [Fact]
    public void 其他环境的变体文件_不加载()
    {
        File.WriteAllText(Path.Combine(_dir, "db.json"),
            """{"Db":{"Key":"base-key"}}""");
        File.WriteAllText(Path.Combine(_dir, "db.feike.json"),
            """{"Db":{"Key":"feike-override"}}""");
        var config = BuildConfig("dev");

        Assert.Equal("base-key", config["Db:Key"]);
    }

    private IConfigurationRoot BuildConfig(string environment)
    {
        var config = new ConfigurationBuilder().AddNexMeshConfig(_dir, environment).Build();
        if (config is IDisposable disposable)
        {
            _disposables.Add(disposable);
        }
        return config;
    }

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXMESH_TEST_URL", null);
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
