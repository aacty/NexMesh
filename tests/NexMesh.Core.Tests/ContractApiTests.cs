namespace NexMesh.Core.Tests;

using System.Net.Http.Json;
using System.Text.Json;

/// <summary>契约端点引擎:路由推导(接口名去I去Api/方法名去Async)、信封包装、响应解包。</summary>
public class ContractApiTests(TestApi api) : IClassFixture<TestApi>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 无模板方法_路由为方法名去Async()
    {
        var response = await api.Client.PostAsync("/api/Demo/GetList", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");

        var envelope = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(0, envelope.GetProperty("code").GetInt32());
        Assert.Equal([1, 2, 3], envelope.GetProperty("data").Deserialize<List<int>>(Json));
    }

    [Fact]
    public async Task 路由大小写不敏感_小写路径同样命中()
    {
        var response = await api.Client.PostAsync("/api/demo/getlist", content: null);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task 同步方法_同样映射并包装信封()
    {
        var response = await api.Client.PostAsync("/api/Demo/Ping", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("pong", envelope.GetProperty("data").GetString());
    }

    [Fact]
    public async Task 非泛型Task_空载荷成功信封()
    {
        var response = await api.Client.PostAsync("/api/Demo/Reset", content: null);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.True(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(0, envelope.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 接口名不带Api后缀_服务名正确推导()
    {
        var response = await api.Client.PostAsync("/api/Plain/Count", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(7, envelope.GetProperty("data").GetInt32());
    }

    [Fact]
    public async Task 模板路由参数_从路由绑定()
    {
        var response = await api.Client.GetAsync("/api/WidgetBiz/w-001");
        Assert.True(response.IsSuccessStatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("widget-w-001", envelope.GetProperty("data").GetString());
    }

    [Fact]
    public async Task 分页响应_字段名为totalCount()
    {
        var response = await api.Client.PostAsync("/api/Crud/Search?keyword=abc", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var page = envelope.GetProperty("data");
        Assert.Equal(1, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("abc", page.GetProperty("items")[0].GetString());
    }

    [Fact]
    public async Task Body对象_POST绑定回传()
    {
        var response = await api.Client.PostAsJsonAsync("/api/Crud/Create", new { name = "OrderX", qty = 7 });
        Assert.True(response.IsSuccessStatusCode);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("OrderX", envelope.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task 信封包含traceId与timestamp()
    {
        var response = await api.Client.PostAsync("/api/Demo/Ping", content: null);
        var envelope = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.True(envelope.TryGetProperty("traceId", out _));
        Assert.True(envelope.TryGetProperty("timestamp", out _));
    }
}

public class ExceptionMiddlewareTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task MeshException_状态码透传_code为HTTP状态码()
    {
        var response = await api.Client.PostAsync("/api/Throw/Biz", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Trace-Id"));
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(400, result.GetProperty("code").GetInt32());
        Assert.Equal("金额必须大于 0", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 未知异常_统一500_不泄露消息()
    {
        var response = await api.Client.PostAsync("/api/Throw/Boom", content: null);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(500, result.GetProperty("code").GetInt32());
        Assert.StartsWith("服务器内部错误", result.GetProperty("message").GetString());
        Assert.DoesNotContain("boom", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 响应已开始后抛异常_保留已写出内容不二次崩溃()
    {
        var response = await api.Client.GetAsync("/stream-then-throw", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("partial", body); // 已开始的部分响应原样送达,不出现 Headers read-only 崩溃
    }
}

/// <summary>异常族语义:类型自带状态码,InfrastructureException 消息不暴露,ValidationException 附 Errors。</summary>
public class ExceptionFamilyTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task BusinessException_400_消息暴露()
    {
        var response = await api.Client.PostAsync("/api/ExceptionFamily/business", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, result.GetProperty("code").GetInt32());
        Assert.Equal("账号不能为空", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task NotFoundException_404()
    {
        var response = await api.Client.PostAsync("/api/ExceptionFamily/not-found", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, result.GetProperty("code").GetInt32());
        Assert.Equal("工单 42 不存在", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InfrastructureException_500_消息不暴露()
    {
        var response = await api.Client.PostAsync("/api/ExceptionFamily/infra", content: null);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(500, result.GetProperty("code").GetInt32());
        Assert.DoesNotContain("连接串", result.GetProperty("message").GetString());
        Assert.StartsWith("服务器内部错误", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ValidationException_400_附字段级Errors()
    {
        var response = await api.Client.PostAsync("/api/ExceptionFamily/validation", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var errors = result.GetProperty("errors");
        var messages = errors.GetProperty("userName").Deserialize<string[]>();
        Assert.NotNull(messages);
        Assert.Contains("必填", messages);
    }
}
