namespace NexMesh.Core.Configuration;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

/// <summary>占位符配置源:内嵌 json 文件源,读取时对值做 ${ENV|默认} 展开。</summary>
internal sealed partial class PlaceholderConfigSource(string directory, string environment) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new PlaceholderConfigProvider(directory, environment);
}

internal sealed partial class PlaceholderConfigProvider : ConfigurationProvider, IDisposable
{
    private readonly IConfigurationRoot _inner;
    private readonly IDisposable? _changeSubscription;

    public PlaceholderConfigProvider(string directory, string environment)
    {
        // SetBasePath 要求绝对路径:相对目录按当前工作目录(= 内容根)解析
        directory = Path.GetFullPath(directory);
        var innerBuilder = new ConfigurationBuilder();
        if (Directory.Exists(directory))
        {
            innerBuilder.SetBasePath(directory);
            var baseFiles = new List<string>();
            var envFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var name = Path.GetFileName(file);
                var dot = name.Split('.');
                if (dot.Length == 2)
                    baseFiles.Add(name); // X.json → 基础配置
                else if (dot.Length == 3 && dot[1].Equals(environment, StringComparison.OrdinalIgnoreCase))
                    envFiles.Add(name); // X.{env}.json → 当前环境变体(后加载覆盖)
            }
            foreach (var name in baseFiles)
                innerBuilder.AddJsonFile(name, optional: true, reloadOnChange: true);
            foreach (var name in envFiles)
                innerBuilder.AddJsonFile(name, optional: true, reloadOnChange: true);
        }
        _inner = innerBuilder.Build();
        _changeSubscription = ChangeToken.OnChange(() => _inner.GetReloadToken(), Load);
        Load();
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _inner.AsEnumerable())
        {
            if (kv.Value is not null)
                data[kv.Key] = Expand(kv.Value);
        }
        Data = data;
        OnReload();
    }

    /// <summary>${ENV_VAR|默认值} 与 ${ENV_VAR} 两种形式:环境变量缺失取默认值(无默认值为空串)。</summary>
    private static string Expand(string value) =>
        value.Contains('$')
            ? PlaceholderRegex().Replace(value, m =>
            {
                var env = Environment.GetEnvironmentVariable(m.Groups[1].Value.Trim());
                return env ?? (m.Groups[2].Success ? m.Groups[2].Value : string.Empty);
            })
            : value;

    [GeneratedRegex(@"\$\{([^}|]+)(?:\|([^}]*))?\}")]
    private static partial Regex PlaceholderRegex();

    public void Dispose()
    {
        _changeSubscription?.Dispose();
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
