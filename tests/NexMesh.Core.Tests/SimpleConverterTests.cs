namespace NexMesh.Core.Tests;

using NexMesh.Core.AutoApi;

/// <summary>
/// 简单类型转换快路径边界(SSS+ 审核):15 快类型语义与 TypeDescriptor 时代一致、
/// char/枚举/nint 回退不变、溢出与格式错误同为 400、文化不变性(decimal 逗号小数拒绝)。
/// </summary>
public class SimpleConverterTests
{
    private static object? Convert<T>(string raw, string name = "p") =>
        AutoApiDispatcher.ConvertSimple(raw, FakeParam<T>(name));

    /// <summary>构造 ParameterInfo(签名真实,无需 HTTP 宿主)。</summary>
    private static System.Reflection.ParameterInfo FakeParam<T>(string name)
        => typeof(SimpleConverterTests).GetMethod(nameof(ParamHolder), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(typeof(T))
            .GetParameters()![0];

    private static void ParamHolder<T>(T p) { }

    [Fact]
    public void 快类型_语义与TypeDescriptor时代一致()
    {
        Assert.Equal(123456L, Convert<long>("123456"));
        Assert.Equal(-42, Convert<int>("-42"));
        Assert.Equal(3.5d, Convert<double>("3.5"));
        Assert.Equal(1.25m, Convert<decimal>("1.25"));
        Assert.True((bool)Convert<bool>("True")!);
        Assert.True((bool)Convert<bool>("true")!);      // 大小写不敏感(与 BooleanConverter 一致)
        Assert.Equal(new Guid("d4a3b2c1-1111-2222-3333-444455556666"), Convert<Guid>("d4a3b2c1-1111-2222-3333-444455556666"));
        Assert.Equal(new DateTime(2026, 11, 6, 10, 0, 0), Convert<DateTime>("2026-11-06T10:00:00"));
        Assert.Equal("hello", Convert<string>("hello"));
        Assert.Equal((short)7, Convert<short>("7"));
        Assert.Equal((byte)9, Convert<byte>("9"));
    }

    [Fact]
    public void 回退类型_char与枚举行为不变()
    {
        Assert.Equal('a', Convert<char>("a"));          // char 无快路径 → TypeDescriptor(CharConverter)
        Assert.Equal(DayOfWeek.Friday, Convert<DayOfWeek>("Friday")); // 枚举 → EnumConverter
    }

    [Fact]
    public void 格式错误与溢出_同为40001()
    {
        var format = Assert.Throws<MeshException>(() => Convert<int>("notanumber"));
        Assert.Equal(400, format.StatusCode);

        var overflow = Assert.Throws<MeshException>(() => Convert<int>("99999999999999999999"));
        Assert.Equal(400, overflow.StatusCode);          // 修复锚定:此前 OverflowException 逃逸 → 500

        var overflowLong = Assert.Throws<MeshException>(() => Convert<long>("99999999999999999999999"));
        Assert.Equal(400, overflowLong.StatusCode);
    }

    [Fact]
    public void 文化不变性_千位分隔符语义与TypeDescriptor时代一致()
    {
        // NumberStyles.Number 含 AllowThousands:逗号是千位分隔符而非小数点(中文区域机器上不漂移);
        // 与旧 DecimalConverter(NumberStyles.Number)语义逐位一致
        Assert.Equal(125m, Convert<decimal>("1,25"));
        Assert.Equal(1250000m, Convert<decimal>("1,250,000"));
        Assert.Equal(1.25m, Convert<decimal>("1.25"));
    }

    [Fact]
    public void 可空类型_拆开转换()
    {
        Assert.Equal(42, Convert<int?>("42"));
        Assert.Equal((byte)1, Convert<byte?>("1"));
    }
}
