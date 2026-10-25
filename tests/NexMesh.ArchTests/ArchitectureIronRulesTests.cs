namespace NexMesh.ArchTests;

using System.Reflection;
using PublicApiGenerator;
using Xunit;

/// <summary>
/// 架构铁律测试:依赖规则从 csproj 注释/文档变成红绿测试——违反时构建直接红。
/// 规则清单见 docs/框架质量契约.md;新增包时在此同步追加规则。
/// </summary>
public class ArchitectureIronRulesTests
{
    private static readonly Assembly CoreAssembly = typeof(NexMesh.Core.AutoApi.AutoApiEndpoint).Assembly;
    private static readonly Assembly AiAssembly = typeof(NexMesh.AI.Tools.NexMeshToolAttribute).Assembly;
    private static readonly Assembly AuthAssembly = typeof(NexMesh.Auth.LoginRateLimiter).Assembly;
    private static readonly Assembly EventsAssembly = typeof(NexMesh.Events.IEventPublisher).Assembly;

    /// <summary>铁律 1:Core 是最底层——不得依赖任何兄弟包(AI/Auth/Cache/Data/Events/Host)。</summary>
    [Fact]
    public void Core_不得依赖兄弟包()
    {
        var result = NetArchTest.Rules.Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "NexMesh.AI", "NexMesh.Auth", "NexMesh.Cache",
                "NexMesh.Data", "NexMesh.Events", "NexMesh.Host")
            .GetResult();
        Assert.True(result.IsSuccessful, "Core 不得依赖兄弟包:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }

    /// <summary>铁律 2:NexMesh.AI 零数据库/缓存依赖(csproj 声明的"零 FreeSql/EFCore/Redis"铁律)。</summary>
    [Fact]
    public void AI核心包_不得依赖数据库与Redis()
    {
        var result = NetArchTest.Rules.Types.InAssembly(AiAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "FreeSql", "Microsoft.EntityFrameworkCore", "StackExchange.Redis",
                "NexMesh.Data", "NexMesh.Cache", "NexMesh.Auth")
            .GetResult();
        Assert.True(result.IsSuccessful, "NexMesh.AI 必须保持零 DB/Redis 依赖:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }

    /// <summary>铁律 3:Auth 不得依赖 AI/Events(认证在 AI 之下)。</summary>
    [Fact]
    public void Auth_不得依赖AI与Events()
    {
        var result = NetArchTest.Rules.Types.InAssembly(AuthAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("NexMesh.AI", "NexMesh.Events")
            .GetResult();
        Assert.True(result.IsSuccessful, "Auth 不得依赖 AI/Events:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }

    /// <summary>铁律 4:Events 是零第三方依赖的纯件——不得依赖 DB/Redis/AI。</summary>
    [Fact]
    public void Events_不得依赖数据库Redis与AI()
    {
        var result = NetArchTest.Rules.Types.InAssembly(EventsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "FreeSql", "Microsoft.EntityFrameworkCore", "StackExchange.Redis",
                "NexMesh.AI", "NexMesh.Auth", "NexMesh.Data", "NexMesh.Cache")
            .GetResult();
        Assert.True(result.IsSuccessful, "NexMesh.Events 必须保持零第三方依赖:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }

    /// <summary>规则 6:NexMesh.Data.Migrations 是数据层特性包——不得依赖 AI/Auth/Cache/Events/Host。</summary>
    [Fact]
    public void Migrations包_不得横向依赖()
    {
        var migrations = typeof(NexMesh.Data.Migrations.NexMeshMigration).Assembly;
        var result = NetArchTest.Rules.Types.InAssembly(migrations)
            .ShouldNot()
            .HaveDependencyOnAny("NexMesh.AI", "NexMesh.Auth", "NexMesh.Cache", "NexMesh.Events", "NexMesh.Host")
            .GetResult();
        Assert.True(result.IsSuccessful, "NexMesh.Data.Migrations 不得横向依赖:" + string.Join(";", result.FailingTypeNames ?? []));
    }

    /// <summary>规则 5:AI.Approval 是 AI 的特性包——只允许依赖 AI/Core 方向,不得横向依赖 Auth/Data/Cache/Events/Host。</summary>
    [Fact]
    public void Approval特性包_不得横向依赖()
    {
        var approval = typeof(NexMesh.AI.Approval.ApprovalGate).Assembly;
        var result = NetArchTest.Rules.Types.InAssembly(approval)
            .ShouldNot()
            .HaveDependencyOnAny(
                "NexMesh.Auth", "NexMesh.Data", "NexMesh.Cache", "NexMesh.Events", "NexMesh.Host")
            .GetResult();
        Assert.True(result.IsSuccessful, "AI.Approval 不得横向依赖兄弟包:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }
}

/// <summary>
/// 公共 API 基线测试:当前程序集公共面与入库基线逐行对比——公共 API 的任何增删改都必须显式更新基线,
/// 破坏性变更(删除/签名变更)在 PR 的基线 diff 里一览无余。
/// 更新方式:设环境变量 NEXMESH_UPDATE_API_BASELINE=1 跑一次测试即重写基线文件。
/// </summary>
public class PublicApiBaselineTests
{
    [Theory]
    [InlineData(typeof(NexMesh.Core.AutoApi.AutoApiEndpoint), "NexMesh.Core.txt")]
    [InlineData(typeof(NexMesh.Auth.LoginRateLimiter), "NexMesh.Auth.txt")]
    [InlineData(typeof(NexMesh.Data.Migrations.NexMeshMigration), "NexMesh.Data.Migrations.txt")]
    public void 公共API面_与基线一致(System.Type markerType, string baselineFile)
    {
        var publicApi = markerType.Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            IncludeAssemblyAttributes = false,
        });
        // 基线位置:优先输出目录(构建拷贝);更新模式写回工程源码目录(入库评审)
        var outputDir = Path.Combine(AppContext.BaseDirectory, "ApiBaselines");
        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ApiBaselines"));

        if (Environment.GetEnvironmentVariable("NEXMESH_UPDATE_API_BASELINE") == "1")
        {
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, baselineFile), publicApi);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, baselineFile), publicApi);
            return; // 重写模式:本次通过,重写后的基线进 Git 评审
        }
        var baselinePath = Path.Combine(outputDir, baselineFile);

        if (!File.Exists(baselinePath))
        {
            Assert.Fail($"基线缺失:{baselinePath};先用 NEXMESH_UPDATE_API_BASELINE=1 生成并提交。");
        }
        var baseline = File.ReadAllText(baselinePath);
        if (string.Equals(baseline, publicApi, StringComparison.Ordinal))
        {
            return;
        }
        var firstDiff = FirstDiff(baseline, publicApi);
        Assert.Fail(
            $"公共 API 面与基线不一致({baselineFile})。首个差异:{firstDiff}\n" +
            "确认是有意变更:设 NEXMESH_UPDATE_API_BASELINE=1 重跑生成新基线并随代码一起提交评审;");
    }

    private static string FirstDiff(string baseline, string current)
    {
        var baselineLines = baseline.Split('\n');
        var currentLines = current.Split('\n');
        for (var i = 0; i < Math.Max(baselineLines.Length, currentLines.Length); i++)
        {
            var a = i < baselineLines.Length ? baselineLines[i].TrimEnd() : "(无)";
            var b = i < currentLines.Length ? currentLines[i].TrimEnd() : "(无)";
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return $"第 {i + 1} 行 基线[{a}] 当前[{b}]";
            }
        }
        return "(未定位)";
    }
}
