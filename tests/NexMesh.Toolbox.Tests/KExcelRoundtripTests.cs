using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using NexMesh.Toolbox;

namespace NexMesh.Toolbox.Tests;

/// <summary>
/// K(第三轮交叉测试):Excel 导入导出往返保真。
/// 断言口径:导出 → 导入后,行集与单元格值和源数据一致(列头映射/文本/数字/日期/空值/超大行数)。
/// </summary>
public class KExcelRoundtripTests
{
    private static async Task<MemoryStream> ExportAsync<T>(IReadOnlyList<T> rows) where T : class
    {
        var ms = new MemoryStream();
        await NexMeshExcel.ExportAsync(ms, rows);
        return ms;
    }

    // ── 列头映射 ──────────────────────────────────────────────────

    private sealed class HeaderRow
    {
        public string UserName { get; set; } = "";

        [ExcelColumnName("部门名称")]
        public string Department { get; set; } = "";
    }

    [Fact]
    public async Task KExcel_列头映射_默认属性名与ExcelColumnName定制()
    {
        var rows = new List<HeaderRow> { new() { UserName = "u1", Department = "护理部" } };
        var ms = await ExportAsync(rows);

        // 无表头模式读原始首行,验证写出的列头文本
        ms.Position = 0;
        var raw = ms.Query(useHeaderRow: false).ToList();
        Assert.Equal(2, raw.Count); // 表头 + 数据
        var header = (IDictionary<string, object>)raw[0]!;
        Assert.Equal("UserName", header["A"]!.ToString());
        Assert.Equal("部门名称", header["B"]!.ToString());

        // 强类型往返
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<HeaderRow>(ms);
        Assert.Single(back);
        Assert.Equal("u1", back[0].UserName);
        Assert.Equal("护理部", back[0].Department);
    }

    // ── 文本保真:中文 / emoji / 换行 / 空白 / 空值 ─────────────────

    private sealed class TextRow
    {
        public string Chinese { get; set; } = "";
        public string Emoji { get; set; } = "";
        public string LineBreak { get; set; } = "";
        public string LeadingTrailing { get; set; } = "";
        public string? NullText { get; set; }
        public string EmptyText { get; set; } = "";
    }

    [Fact]
    public async Task KExcel_文本单元格往返_中文Emoji换行空值()
    {
        var rows = new List<TextRow>
        {
            new()
            {
                Chinese = "护理部-中文测试,全角标点!",
                Emoji = "😀😃🚀田中さん",
                LineBreak = "第一行\n第二行\r\n第三行",
                LeadingTrailing = "  前后空格  ",
                NullText = null,
                EmptyText = "",
            },
        };
        var ms = await ExportAsync(rows);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<TextRow>(ms);

        Assert.Single(back);
        var row = back[0];
        Assert.Equal(rows[0].Chinese, row.Chinese);
        Assert.Equal(rows[0].Emoji, row.Emoji);
        // 实证:\r\n 在 xlsx(XML 层)被归一为 \n,Excel 打开显示无差异——往返保 \n、丢 \r
        Assert.Equal("第一行\n第二行\n第三行", row.LineBreak);
        Assert.Equal(rows[0].LeadingTrailing, row.LeadingTrailing);
        Assert.True(string.IsNullOrEmpty(row.NullText));
        Assert.Equal(string.Empty, row.EmptyText);
    }

    // ── 数字精度 ─────────────────────────────────────────────────

    private sealed class NumberRow
    {
        public decimal DecimalValue { get; set; }
        public double DoubleValue { get; set; }
        public long LongValue { get; set; }
        public int IntValue { get; set; }
    }

    [Fact]
    public async Task KExcel_数字往返_常规小数与整数精确一致()
    {
        var source = new List<NumberRow>
        {
            new() { DecimalValue = 12345.6789m, DoubleValue = 12345.6789, LongValue = 1234567890123L, IntValue = -42 },
            new() { DecimalValue = 0.1m, DoubleValue = 0.1, LongValue = -9876543210L, IntValue = int.MaxValue },
        };
        var ms = await ExportAsync(source);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<NumberRow>(ms);

        Assert.Equal(2, back.Count);
        for (var i = 0; i < source.Count; i++)
        {
            Assert.Equal(source[i].DecimalValue, back[i].DecimalValue);
            Assert.Equal(source[i].DoubleValue, back[i].DoubleValue);
            Assert.Equal(source[i].LongValue, back[i].LongValue);
            Assert.Equal(source[i].IntValue, back[i].IntValue);
        }
    }

    [Fact]
    public async Task KExcel_数字往返_超过Excel双精度的失真与崩溃行为钉住()
    {
        // 实证(xlsx 格式固有):数值单元格本质是 IEEE double(15~16 位有效数字),
        // MiniExcel 读端经 double 桥接转目标类型——超精度即失真,超范围(decimal/long)即 OverflowException。
        static async Task<NumberRow> RoundtripOne(NumberRow row)
        {
            var ms = await ExportAsync(new List<NumberRow> { row });
            ms.Position = 0;
            return (await NexMeshExcel.ImportAsync<NumberRow>(ms))[0];
        }

        Assert.Equal(9007199254740992L, (await RoundtripOne(new NumberRow { LongValue = 9007199254740993L })).LongValue); // 2^53+1 丢 1
        Assert.Equal(1234567890123.46m, (await RoundtripOne(new NumberRow { DecimalValue = 1234567890123.4567m })).DecimalValue); // 17 位有效截断
        Assert.Equal(1e300, (await RoundtripOne(new NumberRow { DoubleValue = 1e300 })).DoubleValue); // double 自身范围往返无损
        Assert.Equal(-1.7976931348623157e308, (await RoundtripOne(new NumberRow { DoubleValue = -1.7976931348623157e308 })).DoubleValue);

        // 崩溃行为钉住(防回归为静默错值):超 decimal/long 范围的数值格导入端抛 OverflowException
        await Assert.ThrowsAsync<OverflowException>(() => RoundtripOne(new NumberRow { LongValue = long.MaxValue }));
        await Assert.ThrowsAsync<OverflowException>(() => RoundtripOne(new NumberRow { DecimalValue = decimal.MaxValue }));
    }

    [Fact]
    public async Task KExcel_长数字以字符串列承载则全保真()
    {
        // 指引钉住:身份证/银行卡等长数字列在 Excel 模型中必须用 string,往返零丢失
        var rows = new List<IdCardRow> { new() { IdCardNo = "110101199001011234" }, new() { IdCardNo = "6222020200112233445" } };
        var ms = await ExportAsync(rows);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<IdCardRow>(ms);

        Assert.Equal(2, back.Count);
        Assert.Equal("110101199001011234", back[0].IdCardNo);
        Assert.Equal("6222020200112233445", back[1].IdCardNo);
    }

    // ── 日期 ─────────────────────────────────────────────────────

    private sealed class DateRow
    {
        public DateTime Time { get; set; }
        public DateTime? NullableTime { get; set; }
    }

    [Fact]
    public async Task KExcel_日期往返_含毫秒与可空()
    {
        var t = new DateTime(2026, 1, 2, 3, 4, 5, 678); // 带毫秒
        var rows = new List<DateRow> { new() { Time = t, NullableTime = t }, new() { Time = t.Date, NullableTime = null } };
        var ms = await ExportAsync(rows);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<DateRow>(ms);

        Assert.Equal(2, back.Count);
        Assert.Equal(t, back[0].Time); // 实证:默认配置是否截断毫秒
        Assert.Equal(t, back[0].NullableTime);
        Assert.Equal(t.Date, back[1].Time);
        Assert.Null(back[1].NullableTime);
    }

    // ── 空集合 / 空列 / 空行 ───────────────────────────────────────

    [Fact]
    public async Task KExcel_空集合导出导入()
    {
        var ms = await ExportAsync(new List<TextRow>());
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<TextRow>(ms);
        Assert.Empty(back);
    }

    [Fact]
    public async Task KExcel_全空值行与稀疏行往返()
    {
        var rows = new List<TextRow>
        {
            new() { Chinese = "有值" },
            new(), // 全空行
            new() { Emoji = "仅一列" },
        };
        var ms = await ExportAsync(rows);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<TextRow>(ms);

        Assert.Equal(3, back.Count); // 实证:全空行是否在往返中被吞掉
        Assert.Equal("有值", back[0].Chinese);
        Assert.Equal("仅一列", back[2].Emoji);
    }

    // ── 65536+ 行边界 ─────────────────────────────────────────────

    private sealed class IdRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public async Task KExcel_超大行数_超过65536行往返完整()
    {
        const int count = 65540; // 老 xls 65536 行上限 + 4
        var rows = Enumerable.Range(0, count).Select(i => new IdRow { Id = i, Name = "行" + i }).ToList();
        var ms = await ExportAsync(rows);
        Assert.True(ms.Length > 0);

        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<IdRow>(ms);

        Assert.Equal(count, back.Count);
        Assert.Equal(0, back[0].Id);
        Assert.Equal(count - 1, back[count - 1].Id);
        Assert.Equal("行65539", back[count - 1].Name);
    }

    private sealed class IdCardRow
    {
        public string IdCardNo { get; set; } = "";
    }

    // ── ExcelIgnore ──────────────────────────────────────────────

    private sealed class IgnoreRow
    {
        public string Visible { get; set; } = "";

        [ExcelIgnore]
        public string Hidden { get; set; } = "";
    }

    [Fact]
    public async Task KExcel_ExcelIgnore列不参与导出导入()
    {
        var rows = new List<IgnoreRow> { new() { Visible = "v", Hidden = "h" } };
        var ms = await ExportAsync(rows);
        ms.Position = 0;
        var back = await NexMeshExcel.ImportAsync<IgnoreRow>(ms);

        Assert.Single(back);
        Assert.Equal("v", back[0].Visible);
        Assert.Equal(string.Empty, back[0].Hidden); // 忽略列不落盘,导入为默认值
    }
}
