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
        // 约定配置是优先级最低的来源:插入源链最前,保证环境变量/命令行(默认源在其后)覆盖它。
        // 文档《配置参考.md》:优先级从低到高 Configs/*.json → 环境变量 → 命令行;
        // 简装入口在 WebApplicationBuilder 默认源之后调用本方法,若追加会反向覆盖环境变量。
        builder.Sources.Insert(0, new PlaceholderConfigSource(directory, environment));
        return builder;
    }
}
