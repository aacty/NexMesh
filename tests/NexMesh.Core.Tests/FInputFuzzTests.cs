namespace NexMesh.Core.Tests;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NexMesh;
using NexMesh.Core.Tests.Fixtures;

/// <summary>Query 标量绑定全类型攻击面夹具:重复键/空值/超长串/数字格式陷阱/bool 变体(端到端 HTTP 语义)。</summary>
public interface IFuzzQueryApi : IServiceApi
{
    Task<int> GetAsync(int id);

    Task<decimal> PriceAsync(decimal price);

    Task<double> RateAsync(double rate);

    Task<bool> FlagAsync(bool flag);

    Task<DateTime> WhenAsync(DateTime when);

    Task<Guid> CodeAsync(Guid code);

    Task<long> ZipAsync(long? zip);

    Task<int> LevelAsync(DayOfWeek day);

    Task<char> InitialAsync(char initial);

    Task<int> DefaultQtyAsync(int qty = 7);

    Task<string> NameAsync(string name);

    [HttpGet("{id:long}")]
    Task<long> RouteAsync(long id);
}

public class FuzzQueryService : IFuzzQueryApi
{
    public Task<int> GetAsync(int id) => Task.FromResult(id);

    public Task<decimal> PriceAsync(decimal price) => Task.FromResult(price);

    public Task<double> RateAsync(double rate) => Task.FromResult(rate);

    public Task<bool> FlagAsync(bool flag) => Task.FromResult(flag);

    public Task<DateTime> WhenAsync(DateTime when) => Task.FromResult(when);

    public Task<Guid> CodeAsync(Guid code) => Task.FromResult(code);

    public Task<long> ZipAsync(long? zip) => Task.FromResult(zip ?? -1);

    public Task<int> LevelAsync(DayOfWeek day) => Task.FromResult((int)day);

    public Task<char> InitialAsync(char initial) => Task.FromResult(initial);

    public Task<int> DefaultQtyAsync(int qty = 7) => Task.FromResult(qty);

    public Task<string> NameAsync(string name) => Task.FromResult(name ?? "");

    public Task<long> RouteAsync(long id) => Task.FromResult(id);
}

/// <summary>
/// Query 绑定输入模糊化(HTTP 端到端,SimpleConverterTests 只覆盖单元语义):
/// 攻击标尺 = 要么正确转换,要么 400 错误信封,绝不 500 / 静默错误值。
/// </summary>
public class FInputFuzzTests(TestApi api) : IClassFixture<TestApi>
{
    private const int OneMb = 1_048_576;

    /// <summary>断言 400 错误信封并返回消息文本。</summary>
    private static async Task<string> AssertBadRequestEnvelopeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(json.GetProperty("success").GetBoolean(), body);
        Assert.Equal(40001, json.GetProperty("code").GetInt32());
        return json.GetProperty("message").GetString() ?? "";
    }

    /// <summary>断言 200 成功信封并返回 data。</summary>
    private static async Task<JsonElement> AssertDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("data");
    }

    [Fact]
    public async Task 重复键_标量Int_取首值()
    {
        // 【修复反转】标量重复键取首值(MVC 语义):?id=1&id=2 → 1
        // (修复前 StringValues.ToString() 逗号连接 "1,2" → int 拒绝)
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?id=1&id=2", content: null));
        Assert.Equal(1, data.GetInt32());
    }

    [Fact]
    public async Task 重复键_标量浮点族_取首值_无静默合并()
    {
        // 【修复反转】重复键取首值:price=1&price=25 → 1(不是 125)、rate=1&rate=5 → 1(不是 15)
        var price = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Price?price=1&price=25", content: null));
        Assert.Equal(1m, price.GetDecimal());
        var rate = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Rate?rate=1&rate=5", content: null));
        Assert.Equal(1.0, rate.GetDouble());
    }

    [Fact]
    public async Task 空值_显式空串_取类型默认值()
    {
        // 宽松语义锁定:?id= → ConvertSimple 空串分支 → DefaultOf(int) → 0(不是 400)
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?id=", content: null));
        Assert.Equal(0, data.GetInt32());
    }

    [Fact]
    public async Task 空值_有默认值参数_取声明默认值()
    {
        // ?qty= → HasDefaultValue → 参数默认值 7
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/DefaultQty?qty=", content: null));
        Assert.Equal(7, data.GetInt32());
    }

    [Fact]
    public async Task 空值_可空类型_交付null()
    {
        // ?zip= → DefaultOf(long?) → null → 业务侧 ?? -1
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Zip?zip=", content: null));
        Assert.Equal(-1, data.GetInt64());
    }

    [Fact]
    public async Task 前导零_按十进制语义()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?id=007", content: null));
        Assert.Equal(7, data.GetInt32());
    }

    [Fact]
    public async Task 十六进制字面量_拒绝为400()
    {
        // "0x1F" 对 int 不可解析(NumberStyles.Integer 无 HexNumber)→ 400 而非 0x1F=31 的静默换基
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?id=0x1F", content: null));
    }

    [Fact]
    public async Task 千分位_int拒绝_decimal文化不变接受()
    {
        // int:NumberStyles.Integer 无 AllowThousands → 400
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?id=1,250", content: null));

        // decimal:NumberStyles.Number 含 AllowThousands → 1250000(与 TypeDescriptor 时代逐位一致)
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Price?price=1,250,000", content: null));
        Assert.Equal(1250000m, data.GetDecimal());
    }

    [Fact]
    public async Task bool变体_yes与1_拒绝为400()
    {
        // 锁定:bool 仅 True/False(大小写不敏感);"yes"/"1" 均拒绝,不静默转换
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Flag?flag=yes", content: null));
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Flag?flag=1", content: null));
    }

    [Fact]
    public async Task bool大小写不变体_接受()
    {
        Assert.True((await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Flag?flag=True", content: null))).GetBoolean());
        Assert.False((await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Flag?flag=FALSE", content: null))).GetBoolean());
    }

    [Fact]
    public async Task 超长查询_1MB字符串_回显保真()
    {
        var huge = new string('a', OneMb);
        var data = await AssertDataAsync(
            await api.Client.PostAsync($"/api/FuzzQuery/Name?name={huge}", content: null));
        Assert.Equal(OneMb, data.GetString()!.Length);
    }

    [Fact]
    public async Task 超长查询_1MB数值参数_拒绝为400()
    {
        // 1MB 数字串 → 溢出/格式异常 → 400(不栈溢出、不 OOM、不 500)
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync($"/api/FuzzQuery/Get?id={new string('9', OneMb)}", content: null));
    }

    [Fact]
    public async Task 查询键大小写不敏感()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Get?ID=5", content: null));
        Assert.Equal(5, data.GetInt32());
    }

    [Fact]
    public async Task Guid变体_N格式接受_垃圾拒绝()
    {
        var data = await AssertDataAsync(await api.Client.PostAsync(
            "/api/FuzzQuery/Code?code=d4a3b2c1111122223333444455556666", content: null));
        Assert.Equal(new Guid("d4a3b2c1-1111-2222-3333-444455556666"), data.GetGuid());

        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Code?code=0x1F", content: null));
    }

    [Fact]
    public async Task DateTime_ISO接受_非法日期拒绝()
    {
        // 带 Z 的值按 DateTime.Parse 默认语义解析为服务器本地时区表示(瞬间不变,表示可带偏移)——
        // 故按时刻断言,机器时区无关(P3 记录:表示形式随服务器时区漂移,瞬间语义正确)
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/When?when=2026-11-06T10:00:00Z", content: null));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(data.GetDateTime()));

        // 13 月 45 日:格式合法但日期非法 → 400 而非静默回滚
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/When?when=2026-13-45", content: null));
    }

    [Fact]
    public async Task DateTime_宽松纯日期_接受()
    {
        // 已知宽松(P3 记录):schema 声称 format=date-time,绑定侧 DateTime.Parse 接受纯日期 —— 锁定当前宽松语义
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/When?when=2026-11-06", content: null));
        Assert.StartsWith("2026-11-06", data.GetString());
    }

    [Fact]
    public async Task char单字符接受_多字符拒绝()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Initial?initial=A", content: null));
        Assert.Equal('A', data.GetString()![0]);

        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Initial?initial=AB", content: null));
    }

    [Fact]
    public async Task 枚举_名称绑定()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Level?day=Friday", content: null));
        Assert.Equal(5, data.GetInt32());
    }

    [Fact]
    public async Task 枚举_数值绑定_宽松语义()
    {
        // 已知宽松(P3 记录):EnumConverter 接受数值字面量,而 schema enum 仅列名称 —— 锁定当前行为
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Level?day=3", content: null));
        Assert.Equal(3, data.GetInt32());
    }

    [Fact]
    public async Task 枚举_超范围数值_宽松语义()
    {
        // 已知宽松(P3 记录):未定义数值 (DayOfWeek)99 亦被接受 —— schema enum 约束比绑定更严
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzQuery/Level?day=99", content: null));
        Assert.Equal(99, data.GetInt32());
    }

    [Fact]
    public async Task 路由long约束_合法通过_非法404()
    {
        // 显式模板替换方法名段:路由为 api/FuzzQuery/{id:long}
        var data = await AssertDataAsync(await api.Client.GetAsync("/api/FuzzQuery/42"));
        Assert.Equal(42L, data.GetInt64());

        // 路由约束在绑定前拒绝:abc → 404(ASP.NET 约束语义,非 400/500)
        var notFound = await api.Client.GetAsync("/api/FuzzQuery/abc");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFound.StatusCode);

        // 20 位数字超 long 上限 → 约束拒绝 → 404(对比 Query 侧无约束为 400)
        var overflow = await api.Client.GetAsync("/api/FuzzQuery/99999999999999999999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, overflow.StatusCode);
    }
}
