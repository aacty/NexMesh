namespace NexMesh.Core.Tests;

using NexMesh.Core.Context;

/// <summary>
/// 攻击面 C(框架架构师 A 对抗性交叉测试):上下文槽嵌套与还原次序。
/// ContextSlotBinding.Enter / AsyncLocalNexMeshContextAccessor.EnterWithTenant /
/// TenantHelper.Enter 共用同一对 AsyncLocal 槽(ContextSlots),还原靠"捕获-还原"作用域对象:
/// DualRestoreScope/RestoreScope/CompositeScope 均无 disposed 守卫、无槽位世代校验——
/// 本测试用生产绑定路径构造嵌套/错序/重复释放场景,实证还原错序与漏还原的具体形态。
/// </summary>
public class ASlotRestoreOrderTests
{
    private static AsyncLocalNexMeshContextAccessor NewAccessor() => new();

    [Fact]
    public void 嵌套双槽绑定_LIFO还原_次序正确_基准()
    {
        var accessor = NewAccessor();
        Assert.Null(accessor.Current);
        Assert.Null(TenantHelper.Current);

        var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-A" };
        var ctxB = new NexMeshContext { UserId = "u-B", TenantId = "hosp-B" };

        using (var outer = ContextSlotBinding.Enter(accessor, ctxA))
        {
            using (var inner = ContextSlotBinding.Enter(accessor, ctxB))
            {
                Assert.Equal("u-B", accessor.Current?.UserId);
                Assert.Equal("hosp-B", TenantHelper.Current);
            }
            Assert.Equal("u-A", accessor.Current?.UserId); // 内层还原到外层
            Assert.Equal("hosp-A", TenantHelper.Current);
        }
        Assert.Null(accessor.Current); // 全部还原
        Assert.Null(TenantHelper.Current);
    }

    [Fact]
    public void 双槽绑定内嵌独立租户作用域_LIFO_还原正确_基准()
    {
        var accessor = NewAccessor();
        var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-A" };

        using (var dual = ContextSlotBinding.Enter(accessor, ctxA))
        using (var tenantScope = TenantHelper.Enter("hosp-inner"))
        {
            Assert.Equal("hosp-inner", TenantHelper.Current);
            Assert.Equal("u-A", accessor.Current?.UserId);
        }
        // 正确 LIFO:同行 using 的释放顺序为 tenantScope → dual,租户先还原到双槽值,双槽再还原到空
        Assert.Null(accessor.Current);
        Assert.Null(TenantHelper.Current);
    }

    /// <summary>
    /// [上轮 P3 实证 → 本轮(架构师 E)已修复,转为回归守卫] 两个作用域共享同一租户槽:
    /// 修复后 RestoreScope/DualRestoreScope 带 disposed 守卫与槽世代校验——错序释放的外层
    /// 作用域只摘除自己(槽当前值 ≠ 它设置的值),活跃的后来者(tenantScope="hosp-inner")
    /// 不再被陈旧快照静默失效。注意:错序场景中内层作用域的 previous 链仍指向历史快照
    /// ("hosp-escape" 由已死亡外层设置),终态由该链决定——世代校验消除的是"活跃绑定被踩掉"
    /// 与"双重 Dispose 陈旧写回"两类实害,彻底消除需栈式作用域管理(超出本轮范围)。
    /// </summary>
    [Fact]
    public void 双槽绑定与租户作用域共用槽位_错序Dispose_世代校验_活跃租户不被踩掉()
    {
        var accessor = NewAccessor();
        var originalTenant = TenantHelper.Current;
        try
        {
            TenantHelper.Set(null);
            var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-escape" };

            var bindingScope = ContextSlotBinding.Enter(accessor, ctxA); // C=u-A, T=hosp-escape
            var tenantScope = TenantHelper.Enter("hosp-inner");          // T=hosp-inner

            // 错序:先释放语义上的外层双槽作用域
            bindingScope!.Dispose(); // [修复后] 租户槽当前值(hosp-inner)≠ 它设置的值 → 只摘除身份槽,不动租户
            Assert.Equal("hosp-inner", TenantHelper.Current);   // 活跃租户作用域不被陈旧快照踩掉
            Assert.Null(accessor.Current?.UserId);              // 身份槽世代匹配,正常还原

            tenantScope.Dispose(); // [修复后] 租户槽当前值 == 它设置的值 → 按先前值链还原(= 外层设置的历史快照)
            Assert.Equal("hosp-escape", TenantHelper.Current);  // 错序场景终态由 previous 链决定(见方法注释)
        }
        finally
        {
            TenantHelper.Set(originalTenant);
        }
    }

    /// <summary>
    /// [上轮 P3 实证 → 本轮(架构师 E)已修复,转为回归守卫] 同一作用域 Dispose 两次:
    /// 修复后第二次为 no-op(_disposed 幂等守卫),其间新绑定(s3="u-C")不再被陈旧快照踩掉。
    /// </summary>
    [Fact]
    public void 作用域重复Dispose_幂等_其间新绑定不被踩掉()
    {
        var accessor = NewAccessor();
        var ctxA = new NexMeshContext { UserId = "u-A" };
        var ctxB = new NexMeshContext { UserId = "u-B" };
        var ctxC = new NexMeshContext { UserId = "u-C" };

        var s1 = ContextSlotBinding.Enter(accessor, ctxA); // C=u-A
        var s2 = ContextSlotBinding.Enter(accessor, ctxB); // C=u-B
        s2!.Dispose();                                     // 正常:C→u-A
        var s3 = accessor.Enter(ctxC);                     // C=u-C(s3 活跃)
        s2!.Dispose();                                     // [修复后] 重复释放:幂等 no-op,不写回陈旧 u-A
        Assert.Equal("u-C", accessor.Current?.UserId);     // 活跃作用域 s3 的值完好
        s3.Dispose();                                      // s3 正常还原到它捕获的 previous=u-A
        Assert.Equal("u-A", accessor.Current?.UserId);
        s1!.Dispose();
        Assert.Null(accessor.Current);
    }

    /// <summary>
    /// [P2 实证] 空快照绑定的不对称语义(NexMeshContext.cs:169-172 + BackgroundRunner.cs:49-50):
    /// 后台分支无快照时,租户槽被显式清空(防继承触发请求的租户),但身份槽"不动"——
    /// 若后台分支从持有身份的流中派生(如请求内触发的后台任务),分支内 accessor.Current
    /// 仍是触发请求的完整身份:系统路径以触发者身份运行(审计字段、按身份的数据访问全部失真)。
    /// 租户防继承而身份不防,语义不对称。
    /// </summary>
    [Fact]
    public void 空快照绑定_租户被清_身份槽继承触发者_不对称()
    {
        var accessor = NewAccessor();
        using (accessor.Enter(new NexMeshContext { UserId = "admin-X", TenantId = "hosp-T" }))
        {
            using (ContextSlotBinding.Enter(accessor, null)) // BackgroundRunner 无快照路径
            {
                Assert.Null(TenantHelper.Current);               // 租户:显式清空(fail-closed)
                Assert.Equal("admin-X", accessor.Current?.UserId); // 身份:[实证] 继承触发请求的完整身份
            }
        }
        Assert.Null(accessor.Current);
        Assert.Null(TenantHelper.Current);
    }
}
