namespace NexMesh.Core.Tests;

using NexMesh.Core.Context;

/// <summary>
/// 攻击面 E(框架架构师 E,资源与泄漏,第二轮交叉测试):槽作用域 API 卫生。
/// 上轮(架构师 A)的 ASlotRestoreOrderTests 实证了缺口后,本轮修复
/// src/NexMesh.Core/Context/NexMeshContext.cs 的三个作用域类:
/// RestoreScope/DualRestoreScope/CompositeScope 加 _disposed 幂等守卫 + 槽世代校验。
/// 本文件用错序/双重释放/交错 Set 等敌对序列验证修复,并确认正常 LIFO 路径不被守卫破坏。
/// </summary>
public class ESlotScopeHygieneTests
{
    private static AsyncLocalNexMeshContextAccessor NewAccessor() => new();

    // ═══════════════════════════ 双重 Dispose 幂等 ═══════════════════════════

    [Fact]
    public void E_租户作用域双重Dispose_幂等_其间Set的新值不被踩掉()
    {
        try
        {
            TenantHelper.Set(null);
            var scope = TenantHelper.Enter("hosp-A");
            scope.Dispose();                     // 正常释放:T→null
            Assert.Null(TenantHelper.Current);

            TenantHelper.Set("hosp-T2");         // 释放后别的路径绑定了新租户
            scope.Dispose();                     // [修复] 第二次释放:no-op,不把陈旧 null 写回
            Assert.Equal("hosp-T2", TenantHelper.Current);
        }
        finally
        {
            TenantHelper.Set(null);
        }
    }

    [Fact]
    public void E_身份作用域双重Dispose_幂等_活跃绑定不被陈旧快照覆盖()
    {
        var accessor = NewAccessor();
        var ctxA = new NexMeshContext { UserId = "u-A" };
        var ctxB = new NexMeshContext { UserId = "u-B" };

        var s1 = accessor.Enter(ctxA);
        var s2 = accessor.Enter(ctxB);
        s1.Dispose();                            // 正常:C→u-B
        Assert.Equal("u-B", accessor.Current?.UserId);
        s1.Dispose();                            // [修复] 重复释放 no-op
        Assert.Equal("u-B", accessor.Current?.UserId);
        s2.Dispose();                            // →u-A
        Assert.Equal("u-A", accessor.Current?.UserId);
        using (accessor.Enter(null!)) { }        // 归位(签名非空,槽实际允许 null)
    }

    [Fact]
    public void E_双槽作用域双重Dispose_幂等_两槽均不二次写回()
    {
        try
        {
            TenantHelper.Set(null);
            var accessor = NewAccessor();
            var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-A" };

            var dual = ContextSlotBinding.Enter(accessor, ctxA); // C→u-A, T→hosp-A
            dual!.Dispose();                     // 正常:C→null, T→null(签名 IDisposable?,实际非 null)
            Assert.Null(accessor.Current);
            Assert.Null(TenantHelper.Current);

            TenantHelper.Set("hosp-later");
            dual!.Dispose();                     // [修复] 重复释放 no-op:两槽不被陈旧快照覆盖
            Assert.Equal("hosp-later", TenantHelper.Current);
            Assert.Null(accessor.Current);
        }
        finally
        {
            TenantHelper.Set(null);
        }
    }

    [Fact]
    public void E_组合作用域双重Dispose_幂等_内层作用域不被重复还原()
    {
        var accessor = new CustomAccessor();     // 非 AsyncLocal accessor:ContextSlotBinding 走 CompositeScope 组合路径
        var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-A" };

        var composite = ContextSlotBinding.Enter(accessor, ctxA);
        composite!.Dispose();                    // (签名 IDisposable?,实际非 null)
        Assert.Null(accessor.Current);           // 还原到 Enter 前的 null
        composite!.Dispose();                    // [修复] 重复释放 no-op(内层 RestoreScope 亦各自幂等)
        Assert.Null(accessor.Current);
    }

    // ═══════════════════════════ 错序释放:世代校验只摘除自己 ═══════════════════════════

    [Fact]
    public void E_租户槽错序释放_外层陈旧值不覆盖新值()
    {
        try
        {
            TenantHelper.Set(null);
            var outer = TenantHelper.Enter("hosp-A");  // T→hosp-A
            var inner = TenantHelper.Enter("hosp-B");  // T→hosp-B

            outer.Dispose();                     // [修复] 错序:T 当前(hosp-B)≠ outer 设置值 → 跳过还原
            Assert.Equal("hosp-B", TenantHelper.Current);

            inner.Dispose();                     // 活跃内层正常:T→hosp-A(previous 链,错序固有歧义)
            Assert.Equal("hosp-A", TenantHelper.Current);
        }
        finally
        {
            TenantHelper.Set(null);
        }
    }

    [Fact]
    public void E_身份槽错序释放_外层陈旧值不覆盖新值()
    {
        var accessor = NewAccessor();
        var ctxA = new NexMeshContext { UserId = "u-A" };
        var ctxB = new NexMeshContext { UserId = "u-B" };

        var outer = accessor.Enter(ctxA);
        var inner = accessor.Enter(ctxB);
        outer.Dispose();                         // [修复] 错序:C 当前(u-B)≠ outer 设置值 → 跳过还原
        Assert.Equal("u-B", accessor.Current?.UserId);
        inner.Dispose();                         // →u-A
        Assert.Equal("u-A", accessor.Current?.UserId);
        using (accessor.Enter(null!)) { }        // 归位(签名非空,槽实际允许 null)
    }

    [Fact]
    public void E_双槽作用域错序释放_身份槽还原_租户槽世代跳过()
    {
        try
        {
            TenantHelper.Set(null);
            var accessor = NewAccessor();
            var ctxA = new NexMeshContext { UserId = "u-A", TenantId = "hosp-A" };

            var dual = ContextSlotBinding.Enter(accessor, ctxA); // C→u-A, T→hosp-A
            var tenantScope = TenantHelper.Enter("hosp-inner");  // T→hosp-inner(后来者改写租户槽)

            dual!.Dispose();                     // [修复] 逐槽世代校验:T 当前(hosp-inner)≠ 设置值 → 跳过;C 世代匹配 → 还原(签名 IDisposable?,实际非 null)
            Assert.Equal("hosp-inner", TenantHelper.Current);    // 活跃租户不被踩掉
            Assert.Null(accessor.Current?.UserId);

            tenantScope.Dispose();               // 租户槽世代匹配 → 按先前值链还原(hosp-A,外层设置的历史快照)
            Assert.Equal("hosp-A", TenantHelper.Current);
        }
        finally
        {
            TenantHelper.Set(null);
        }
    }

    // ═══════════════════════════ 守卫不破坏正常路径 ═══════════════════════════

    [Fact]
    public void E_设置值等于先前值_释放后无变化_双重释放仍幂等()
    {
        try
        {
            TenantHelper.Set("hosp-same");
            var scope = TenantHelper.Enter("hosp-same");
            Assert.Equal("hosp-same", TenantHelper.Current);
            scope.Dispose();                     // 当前==设置==previous:还原无变化
            Assert.Equal("hosp-same", TenantHelper.Current);
            scope.Dispose();
            Assert.Equal("hosp-same", TenantHelper.Current);
        }
        finally
        {
            TenantHelper.Set(null);
        }
    }

    [Fact]
    public void E_三层嵌套LIFO_正常顺序释放_还原链条完好()
    {
        var accessor = NewAccessor();
        var ctx0 = new NexMeshContext { UserId = "u-0" };
        var ctx1 = new NexMeshContext { UserId = "u-1", TenantId = "hosp-1" };
        var ctx2 = new NexMeshContext { UserId = "u-2", TenantId = "hosp-2" };
        var ctx3 = new NexMeshContext { UserId = "u-3", TenantId = "hosp-3" };

        using (accessor.Enter(ctx0))
        {
            using (ContextSlotBinding.Enter(accessor, ctx1))
            using (ContextSlotBinding.Enter(accessor, ctx2))
            {
                Assert.Equal("u-2", accessor.Current?.UserId);
                Assert.Equal("hosp-2", TenantHelper.Current);
                var s3 = ContextSlotBinding.Enter(accessor, ctx3);
                Assert.Equal("u-3", accessor.Current?.UserId);
                s3!.Dispose();
                Assert.Equal("u-2", accessor.Current?.UserId);
                Assert.Equal("hosp-2", TenantHelper.Current);
            }
            // 内层三层按 LIFO 还原后,应回到最外层 ctx0 的绑定(守卫对正常路径零影响)
            Assert.Equal("u-0", accessor.Current?.UserId);
            Assert.Null(TenantHelper.Current);
        }
        // 最外层 using 也退出:回到测试初始状态
        Assert.Null(accessor.Current);
        Assert.Null(TenantHelper.Current);
    }

    /// <summary>非 AsyncLocal accessor:ContextSlotBinding 对自定义实现回退为两次 Enter + CompositeScope。</summary>
    private sealed class CustomAccessor : INexMeshContextAccessor
    {
        private NexMeshContext? _current;

        public NexMeshContext? Current => _current;

        public IDisposable Enter(NexMeshContext context)
        {
            var previous = _current;
            _current = context;
            return new Restore(this, previous);
        }

        private sealed class Restore(CustomAccessor owner, NexMeshContext? previous) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }
                owner._current = previous;
            }
        }
    }
}
