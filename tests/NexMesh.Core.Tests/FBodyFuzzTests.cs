namespace NexMesh.Core.Tests;

using System.Text;
using System.Text.Json;
using NexMesh;
using NexMesh.Core.Tests.Fixtures;

/// <summary>Body JSON 攻击面夹具:深度嵌套/超大字符串/Content-Type 谎称/空 body(端到端 HTTP 语义)。</summary>
public interface IFuzzBodyApi : IServiceApi
{
    Task<FuzzEchoDto> EchoAsync(FuzzEchoDto dto);

    Task<string> ShapeAsync(object shape);
}

public class FuzzEchoDto
{
    public string Name { get; set; } = "";

    public int Qty { get; set; }
}

public class FuzzBodyService : IFuzzBodyApi
{
    public Task<FuzzEchoDto> EchoAsync(FuzzEchoDto dto) => Task.FromResult(dto!);

    public Task<string> ShapeAsync(object shape) => Task.FromResult(shape?.ToString() ?? "<null>");
}

/// <summary>
/// Body JSON 输入攻击(HTTP 端到端):攻击标尺 = 要么正确解析,要么 400 错误信封,绝不 500 / 栈溢出。
/// </summary>
public class FBodyFuzzTests(TestApi api) : IClassFixture<TestApi>
{
    private const int TwoMb = 2 * 1_048_576;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

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
    public async Task 深层嵌套_数组5000层_返回400而非栈溢出()
    {
        // System.Text.Json 默认 MaxDepth=64 → JsonException → 400 信封;不得 StackOverflow(无法捕获)或 500
        var deep = new string('[', 5000) + new string(']', 5000);
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Shape", Json(deep)));
        Assert.Contains("JSON", message);
    }

    [Fact]
    public async Task 深层嵌套_对象5000层_返回400()
    {
        var deep = new string('{', 5000) + "\"a\":" + "1" + new string('}', 5000);
        await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Shape", Json(deep)));
    }

    [Fact]
    public async Task 超大字符串字段_2MB_解析保真()
    {
        var huge = new string('x', TwoMb);
        var data = await AssertDataAsync(await api.Client.PostAsync(
            "/api/FuzzBody/Echo", Json($$"""{"name":"{{huge}}","qty":1}""")));
        Assert.Equal(TwoMb, data.GetProperty("name").GetString()!.Length);
        Assert.Equal(1, data.GetProperty("qty").GetInt32());
    }

    [Fact]
    public async Task Content_Type谎称_textplain载合法JSON_返回400而非500()
    {
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Echo", new StringContent(
                """{"name":"liar","qty":1}""", Encoding.UTF8, "text/plain")));
        Assert.NotNull(message);
    }

    [Fact]
    public async Task 无Content_Type头_返回400而非500()
    {
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Echo", new ByteArrayContent(
                Encoding.UTF8.GetBytes("""{"name":"noct","qty":1}"""))));
        Assert.NotNull(message);
    }

    [Fact]
    public async Task 空body_applicationjson_返回400()
    {
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Echo", Json("")));
        Assert.Contains("JSON", message);
    }

    [Fact]
    public async Task JSON_null_body_宽松接受()
    {
        // 已知宽松(P3 记录):requestBody required=true 但绑定侧不校验 null 字面量 → 200 + data=null
        var data = await AssertDataAsync(
            await api.Client.PostAsync("/api/FuzzBody/Echo", Json("null")));
        Assert.Equal(JsonValueKind.Null, data.ValueKind);
    }

    [Fact]
    public async Task multipart缺body字段_返回400而非500()
    {
        // 【修复反转】空 multipart(缺必需 Body 字段)→ 40001 表单解析失败
        // (修复前:ReadForm 异常族/缺字段 null 进业务 NRE → 50000)
        using var content = new MultipartFormDataContent();
        var response = await api.Client.PostAsync("/api/FuzzBody/Echo", content);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(40001, json.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 伪multipart垃圾体_返回400而非500()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a multipart body"));
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=X-FAKE-BOUNDARY");
        var message = await AssertBadRequestEnvelopeAsync(
            await api.Client.PostAsync("/api/FuzzBody/Echo", content));
        Assert.NotNull(message);
    }

    [Fact]
    public async Task 加号json后缀_内容类型接受()
    {
        // HasJsonContentType 语义:application/*+json 亦为合法 JSON —— 锁定接受面
        var data = await AssertDataAsync(await api.Client.PostAsync("/api/FuzzBody/Echo",
            new StringContent("""{"name":"vendor","qty":2}""", Encoding.UTF8, "application/vnd.api+json")));
        Assert.Equal("vendor", data.GetProperty("name").GetString());
        Assert.Equal(2, data.GetProperty("qty").GetInt32());
    }
}
