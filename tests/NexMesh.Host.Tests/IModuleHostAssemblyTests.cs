namespace NexMesh.Host.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;

/// <summary>排序验证模块:Phase=Platform,Priority=2。</summary>
public sealed class ISeqPlatformLate : AppModule
{
    public override string Name => "ISeqPlatformLate";

    public override ModulePhase Phase => ModulePhase.Platform;

    public override int Priority => 2;

    public override void ConfigureServices(NexMeshBuilder builder) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureServices:ISeqPlatformLate");

    public override void ConfigureApplication(WebApplication app) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureApplication:ISeqPlatformLate");
}

/// <summary>排序验证模块:Phase=Platform,Priority=1(应先于同 Phase 的 Priority=2)。</summary>
public sealed class ISeqPlatformEarly : AppModule
{
    public override string Name => "ISeqPlatformEarly";

    public override ModulePhase Phase => ModulePhase.Platform;

    public override int Priority => 1;

    public override void ConfigureServices(NexMeshBuilder builder) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureServices:ISeqPlatformEarly");

    public override void ConfigureApplication(WebApplication app) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureApplication:ISeqPlatformEarly");
}

/// <summary>排序验证模块:Phase=Security(晚于 Platform 阶段)。</summary>
public sealed class ISeqSecurity : AppModule
{
    public override string Name => "ISeqSecurity";

    public override ModulePhase Phase => ModulePhase.Security;

    public override void ConfigureServices(NexMeshBuilder builder) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureServices:ISeqSecurity");

    public override void ConfigureApplication(WebApplication app) =>
        IModuleHostAssemblyTests.Log.Enqueue("ConfigureApplication:ISeqSecurity");
}

/// <summary>重复挂载验证模块:测试 AddModule 同类型多次挂载的去重。</summary>
public sealed class IDupMountModule : AppModule
{
}

/// <summary>
/// 模块宿主装配攻击面(交叉测试 I):装配顺序确定性、重复装配防护、端口语义。
/// </summary>
public class IModuleHostAssemblyTests
{
    // 多个测试类并行宿主都会扫描到 ISeq* 模块:并发队列保序,断言按"第 i 次出现"配对,抗批次交错
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> Log = new();

    private static int[] IndexesOf(string[] items, string value) =>
        [.. items.Select((v, i) => (v, i)).Where(x => x.v == value).Select(x => x.i)];

    [Fact]
    public void 模块装配_同类型重复挂载_仅保留一份()
    {
        var mb = new NexMeshBuilder(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            new ConfigurationBuilder().Build());

        mb.AddModule<IDupMountModule>().AddModule<IDupMountModule>().AddModule(new IDupMountModule());

        Assert.Equal(1, mb.Modules.Count(m => m is IDupMountModule));
    }

    [Fact]
    public void 模块装配_显式挂载与自动扫描交叉_类型去重()
    {
        var mb = new NexMeshBuilder(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            new ConfigurationBuilder().Build());

        mb.AddModule<IDupMountModule>();
        mb.AutoScanModules(typeof(IModuleHostAssemblyTests).Assembly);

        Assert.Equal(1, mb.Modules.Count(m => m is IDupMountModule));
    }

    [Fact]
    public void 模块两阶段_按Phase再Priority升序执行()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]);
        using var app = builder.Build();
        app.UseNexMeshHost();

        var all = Log.ToArray();
        foreach (var stage in new[] { "ConfigureServices", "ConfigureApplication" })
        {
            var early = IndexesOf(all, $"{stage}:ISeqPlatformEarly");
            var late = IndexesOf(all, $"{stage}:ISeqPlatformLate");
            var sec = IndexesOf(all, $"{stage}:ISeqSecurity");

            Assert.NotEmpty(early);
            Assert.Equal(early.Length, late.Length);
            Assert.Equal(early.Length, sec.Length);
            for (var i = 0; i < early.Length; i++)
            {
                Assert.True(early[i] < late[i],
                    $"{stage} Platform 内 Priority 升序失效(第{i + 1}批):{string.Join(',', all)}");
                Assert.True(late[i] < sec[i],
                    $"{stage} Phase 排序失效(Platform 应先于 Security,第{i + 1}批):{string.Join(',', all)}");
            }
        }
    }

    [Fact]
    public void AddNexMeshHost_重复调用_fail_fast()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]);

        // 重复装配 = 约定服务/契约/模块全量双份注册,必须启动早期暴露
        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void UseNexMeshHost_重复调用_fail_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]);
        using var app = builder.Build();

        app.UseNexMeshHost();

        // 二次执行会重复跑模块 ConfigureApplication(建表/种子/审计等业务初始化)
        Assert.Throws<InvalidOperationException>(() => app.UseNexMeshHost());
    }

    [Fact]
    public void HttpPort与GrpcPort同值_装配阶段fail_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:HttpPort"] = "27801";
        builder.Configuration["NexMesh:GrpcPort"] = "27801";

        // 同值双 ListenAnyIP 必然地址冲突,应装配期给出可定位错误而非运行期 IOException
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]));
        Assert.Contains("27801", ex.Message);
    }

    [Fact]
    public void GrpcPort大于0_未装配gRPC面_装配正常_仅HTTP面()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:GrpcPort"] = "27802";
        builder.AddNexMeshHost([typeof(IModuleHostAssemblyTests).Assembly]);
        using var app = builder.Build();

        // 文档语义:GrpcPort>0 且装配了 gRPC 面才追加 Http2 监听;未装配时静默跳过
        Assert.Null(app.Services.GetService<NexMeshGrpcExtensions.ContractGrpcRegistry>());
    }
}
