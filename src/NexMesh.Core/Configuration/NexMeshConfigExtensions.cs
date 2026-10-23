namespace NexMesh.Core.Configuration;

using Microsoft.Extensions.Configuration;

/// <summary>
/// 约定配置:加载 {directory} 下全部 X.json(基础)与 X.{env}.json(当前环境变体,后者覆盖前者),
/// 支持 ${ENV_VAR|默认值} 占位符展开与 reloadOnChange 热更新。目录不存在时安全跳过。
/// </summary>
public static class NexMeshConfigExtensions
{
    public static IConfigurationBuilder AddNexMeshConfig(
        this IConfigurationBuilder builder,
        string directory = "Configs",
        string? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                         ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                         ?? "Production";
        return builder.Add(new PlaceholderConfigSource(directory, environment));
    }
}
