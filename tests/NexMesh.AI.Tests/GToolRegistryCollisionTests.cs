namespace NexMesh.AI.Tests;

using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI.Tools;

/// <summary>
/// G(架构师二轮交叉测试)工具注册表同名冲突 fail-fast:
/// 两个 [NexMeshTool] 方法映射到同一工具名(同名 / 去 Async 后缀后同名 / 仅大小写不同)必须注册即抛 NotSupportedException,
/// 消息含两个冲突类型的全名;同一方法重复发现(同一注册表多次 Discover)按幂等处理。
/// 探针类型用 Reflection.Emit 动态程序集构造——绝不向本测试程序集写入静态 [NexMeshTool]
/// (否则会被 ToolSearchTests/ResilienceTests 等扫描本程序集的注册表连带扫到,污染既有测试)。
/// </summary>
public class GToolRegistryCollisionTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:AI:MaxToolsPerRequest"] = "100",
            ["NexMesh:AI:Providers:main:Type"] = "stub",
            ["NexMesh:AI:Providers:main:Model"] = "stub-model",
        }).Build();

    // ── Reflection.Emit 探针工厂 ─────────────────────────────────────

    private static readonly ConstructorInfo ToolAttrCtor =
        typeof(NexMeshToolAttribute).GetConstructor([typeof(string), typeof(NexMeshRiskLevel)])!;

    private static readonly MethodInfo TaskFromResultOfString = typeof(Task)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Task.FromResult) && m.IsGenericMethodDefinition)
        .MakeGenericMethod(typeof(string));

    private static (AssemblyBuilder Asm, ModuleBuilder Module) NewModule()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GDynAsm"), AssemblyBuilderAccess.Run);
        return (asm, asm.DefineDynamicModule("GDynModule"));
    }

    /// <summary>静态工具方法:public static Task&lt;string&gt; {name}() => Task.FromResult(value)。</summary>
    private static void DefineStaticTool(TypeBuilder tb, string name, string description, string returnValue)
    {
        var method = tb.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
            typeof(Task<string>), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, returnValue);
        il.Emit(OpCodes.Call, TaskFromResultOfString);
        il.Emit(OpCodes.Ret);
        method.SetCustomAttribute(new CustomAttributeBuilder(ToolAttrCtor, [description, NexMeshRiskLevel.Low]));
    }

    /// <summary>实例工具方法:public Task&lt;string&gt; {name}() => Task.FromResult(value)。</summary>
    private static void DefineInstanceTool(TypeBuilder tb, string name, string description, string returnValue)
    {
        var method = tb.DefineMethod(name, MethodAttributes.Public,
            typeof(Task<string>), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, returnValue);
        il.Emit(OpCodes.Call, TaskFromResultOfString);
        il.Emit(OpCodes.Ret);
        method.SetCustomAttribute(new CustomAttributeBuilder(ToolAttrCtor, [description, NexMeshRiskLevel.Low]));
    }

    private static Type EmitStaticToolType(
        ModuleBuilder module, string fullName, string methodName, string description, string returnValue)
    {
        var tb = module.DefineType(fullName, TypeAttributes.Public | TypeAttributes.Sealed);
        DefineStaticTool(tb, methodName, description, returnValue);
        return tb.CreateType()!;
    }

    private static Type EmitInstanceToolType(
        ModuleBuilder module, string fullName, string methodName, string description, string returnValue)
    {
        var tb = module.DefineType(fullName, TypeAttributes.Public | TypeAttributes.Sealed);
        tb.DefineDefaultConstructor(MethodAttributes.Public);
        DefineInstanceTool(tb, methodName, description, returnValue);
        return tb.CreateType()!;
    }

    // ── 冲突检测 ─────────────────────────────────────────────────────

    [Fact]
    public void 两个类型同名静态工具_注册即抛_消息含两个类型全名()
    {
        var (_, module) = NewModule();
        EmitStaticToolType(module, "GDyn.GA", "GDupAsync", "A 版本", "a");
        EmitStaticToolType(module, "GDyn.GB", "GDupAsync", "B 版本", "b");
        var registry = new NexMeshToolRegistry(Config(), module.Assembly);

        var ex = Assert.Throws<NotSupportedException>(() => registry.DiscoverTools());

        Assert.Contains("[NexMesh Tool Registry]", ex.Message);
        Assert.Contains("GDup", ex.Message);
        Assert.Contains("GDyn.GA", ex.Message); // 两个冲突类型全名都在消息里
        Assert.Contains("GDyn.GB", ex.Message);
    }

    [Fact]
    public void DoX与DoXAsync_去Async后缀后同名_注册即抛()
    {
        var (_, module) = NewModule();
        // 同一类型内 DoX 与 DoXAsync:两者都被 M.E.AI 去 Async 后缀映射为 "GDupOp"
        var tb = module.DefineType("GDyn.GSuffix", TypeAttributes.Public | TypeAttributes.Sealed);
        DefineStaticTool(tb, "GDupOpAsync", "异步版本", "a");
        DefineStaticTool(tb, "GDupOp", "同步版本", "b");
        tb.CreateType();
        var registry = new NexMeshToolRegistry(Config(), module.Assembly);

        var ex = Assert.Throws<NotSupportedException>(() => registry.DiscoverAllTools());

        Assert.Contains("GDupOp", ex.Message);
        Assert.Contains("GDyn.GSuffix", ex.Message);
    }

    [Fact]
    public void 仅大小写不同的工具名_同样判冲突_注册表按忽略大小写查找()
    {
        var (_, module) = NewModule();
        EmitStaticToolType(module, "GDyn.GCaseA", "GCaseOpAsync", "大写版本", "a");
        EmitStaticToolType(module, "GDyn.GCaseB", "gcaseopAsync", "小写版本", "b");
        var registry = new NexMeshToolRegistry(Config(), module.Assembly);

        // _rawFunctions 为 OrdinalIgnoreCase:GCaseOp 与 gcaseop 占同一查找槽位,必须 fail-fast
        Assert.Throws<NotSupportedException>(() => registry.DiscoverTools());
    }

    [Fact]
    public void 实例方法同名工具_AddNexMeshAi启动验证期即抛()
    {
        var (_, module) = NewModule();
        EmitInstanceToolType(module, "GDyn.GF1", "GInstDupAsync", "F1 版本", "f1");
        EmitInstanceToolType(module, "GDyn.GF2", "GInstDupAsync", "F2 版本", "f2");

        var services = new ServiceCollection();

        // 验证模式(services == null)同样登记实例工具名占用:同名冲突在启动期暴露,不等运行期
        var ex = Assert.Throws<NotSupportedException>(
            () => services.AddNexMeshAi(Config(), module.Assembly));

        Assert.Contains("GDyn.GF1", ex.Message);
        Assert.Contains("GDyn.GF2", ex.Message);
    }

    [Fact]
    public void 实例方法同名工具_运行期注册表同样抛()
    {
        var (_, module) = NewModule();
        var typeF1 = EmitInstanceToolType(module, "GDyn.GF3", "GInstDup2Async", "F3 版本", "f3");
        var typeF4 = EmitInstanceToolType(module, "GDyn.GF4", "GInstDup2Async", "F4 版本", "f4");
        var services = new ServiceCollection();
        services.AddScoped(typeF1);
        services.AddScoped(typeF4);
        var registry = new NexMeshToolRegistry(Config(), services.BuildServiceProvider(), module.Assembly);

        Assert.Throws<NotSupportedException>(() => registry.DiscoverAllTools());
    }

    // ── 幂等与正常路径(防 fail-fast 误伤重复发现) ───────────────────

    [Fact]
    public void 同一注册表多次发现_不抛且结果一致()
    {
        var (_, module) = NewModule();
        EmitStaticToolType(module, "GDyn.GOk1", "GFree1Async", "工具1", "1");
        EmitStaticToolType(module, "GDyn.GOk2", "GFree2Async", "工具2", "2");
        var registry = new NexMeshToolRegistry(Config(), module.Assembly);

        var first = registry.DiscoverAllTools().ToList();
        var second = registry.DiscoverAllTools().ToList(); // 同一注册表重复 Discover(审批测试逐次调用的形态)

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(t => t.Name), second.Select(t => t.Name));
        Assert.NotNull(registry.FindRawFunction("GFree1"));
        Assert.NotNull(registry.FindRawFunction("gfree2")); // OrdinalIgnoreCase 查找
        Assert.Null(registry.FindRawFunction("GFreeX"));
    }

    [Fact]
    public void 不同名多类型工具_全部正常注册()
    {
        var (_, module) = NewModule();
        EmitInstanceToolType(module, "GDyn.GOk3", "GInst1Async", "实例工具", "x");
        EmitStaticToolType(module, "GDyn.GOk4", "GStatic1Async", "静态工具", "y");
        var services = new ServiceCollection();
        services.AddScoped(module.Assembly.GetType("GDyn.GOk3")!);
        var registry = new NexMeshToolRegistry(Config(), services.BuildServiceProvider(), module.Assembly);

        var tools = registry.DiscoverAllTools().ToList();

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "GInst1");
        Assert.Contains(tools, t => t.Name == "GStatic1");
    }
}
