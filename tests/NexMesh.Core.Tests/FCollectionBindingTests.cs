namespace NexMesh.Core.Tests;

using System.Text.Json;
using NexMesh;
using NexMesh.Core.Tests.Fixtures;

/// <summary>集合 Query 绑定攻击面夹具:混合形态/千元素/坏元素/空值(端到端 HTTP 语义)。</summary>
public interface IFuzzCollectionApi : IServiceApi
{
    Task<int> SumAsync(int[] ids);

    Task<int> FlagsSumAsync(HashSet<int> flags);

    Task<int> SeqSumAsync(IEnumerable<int> seq);

    Task<string> JoinAsync(List<string> parts);

    Task<double> RatesAsync(double[] rates);
}

public class FuzzCollectionService : IFuzzCollectionApi
{
    public Task<int> SumAsync(int[] ids) => Task.FromResult(ids?.Sum() ?? 0);

    public Task<int> FlagsSumAsync(HashSet<int> flags) => Task.FromResult(flags?.Sum() ?? 0);

    public Task<int> SeqSumAsync(IEnumerable<int> seq) => Task.FromResult(seq?.Sum() ?? 0);

    public Task<string> JoinAsync(List<string> parts) => Task.FromResult(string.Join("|", parts ?? []));

    public Task<double> RatesAsync(double[] rates) => Task.FromResult(rates?.Sum() ?? 0);
}

/// <summary>
/// 集合绑定输入攻击(HTTP 端到端):攻击标尺 = 要么正确转换,要么 400 错误信封,绝不 500 / 静默错误值。
/// </summary>
public class FCollectionBindingTests(TestApi api) : IClassFixture<TestApi>
{
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

    private static async Task<JsonElement> AssertDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("data");
    }

    [Fact]
    public async Task 混合形态_逗号串加重复键_统一拍平求和()
    {
        // 【修复反转】混合形态统一拍平(此前 decimal 形态经 AllowThousands 静默变错误值,
        // int 形态拒绝):ids=1,2,3&ids=4 → [1,2,3,4] → 求和 10
        var data = await AssertDataAsync(await api.Client.PostAsync(
            "/api/FuzzCollection/Sum?ids=1,2,3&ids=4", content: null));
        Assert.Equal(10, data.GetInt32());
    }

    [Fact]
    public async Task 千元素_逗号分隔_求和保真()
    {
        var data = await AssertDataAsync(await api.Client.PostAsync(
            $"/api/FuzzCollection/Sum?ids={string.Join(",", Enumerable.Range(0, 1000))}", content: null));
        Assert.Equal(499500, data.GetInt32());
    }

    [Fact]
    public async Task 五百重复键_求和保真()
    {
        var query = string.Join("&", Enumerable.Range(0, 500).Select(i => $"ids={i}"));
        var data = await AssertDataAsync(
            await api.Client.PostAsync($"/api/FuzzCollection/Sum?{query}", content: null));
        Assert.Equal(124750, data.GetInt32());
    }

    [Fact]
    public async Task 坏元素_拒绝为400信封()
    {
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzCollection/Sum?ids=1,x,3", content: null));
        Assert.Contains("集合元素", message);
    }

    [Fact]
    public async Task 空值_空集合()
    {
        // ?ids= → 单值空串 → RemoveEmptyEntries → 空数组(锁定)
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/Sum?ids=", content: null));
        Assert.Equal(0, data.GetInt32());
    }

    [Fact]
    public async Task 尾随空值_两种形态_统一去空对称()
    {
        // 【修复反转】混合形态统一拍平且 RemoveEmptyEntries:逗号尾随空与重复键空项行为对称
        var comma = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/Sum?ids=1,", content: null));
        Assert.Equal(1, comma.GetInt32());

        var dup = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/Sum?ids=1&ids=", content: null));
        Assert.Equal(1, dup.GetInt32());
    }

    [Fact]
    public async Task HashSet_期望去重求和200()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/FlagsSum?flags=1,1,2", content: null));
        Assert.Equal(3, data.GetInt32());
    }

    [Fact]
    public async Task IEnumerable接口形态_正常绑定()
    {
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/SeqSum?seq=4,5,6", content: null));
        Assert.Equal(15, data.GetInt32());
    }

    [Fact]
    public async Task 缺省集合参数_交付null_业务判空()
    {
        // 锁定契约语义:未提供 → null 交付(框架约定"业务自行判空");
        // 风险注记(P3):未判空的实现(如 AdvancedBindingTests 的 SumAsync)将 NRE → 500
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzCollection/Sum", content: null));
        Assert.Equal(0, data.GetInt32());
    }

    [Fact]
    public async Task double集合_混合形态_统一拍平求和()
    {
        // 【修复反转】拍平后 [1,2,3] → 求和 6(修复前 "1,2" 经 AllowThousands 静默解析成 12)
        var data = await AssertDataAsync(await api.Client.PostAsync(
            "/api/FuzzCollection/Rates?rates=1,2&rates=3", content: null));
        Assert.Equal(6, data.GetDouble());
    }
}
