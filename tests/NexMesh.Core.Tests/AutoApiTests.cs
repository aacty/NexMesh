namespace NexMesh.Core.Tests;

using System.Text.Json;

public class AutoApiTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task 无参Task方法_自动映射并返回JSON()
    {
        var response = await api.Client.PostAsync("/api/demo/getlistasync", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        var list = JsonSerializer.Deserialize<List<int>>(body);
        Assert.Equal([1, 2, 3], list);
    }

    [Fact]
    public async Task 同步方法_返回JSON()
    {
        var response = await api.Client.PostAsync("/api/demo/ping", content: null);
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pong", body);
    }

    [Fact]
    public async Task 非泛型Task_返回204()
    {
        var response = await api.Client.PostAsync("/api/demo/resetasync", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task 未标注ServiceRoute_使用类名默认路由()
    {
        var response = await api.Client.PostAsync("/api/plainapiservice/countasync", content: null);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("7", body);
    }
}

public class ExceptionMiddlewareTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task MeshException_业务码与状态码透传_并写入TraceId头()
    {
        var response = await api.Client.PostAsync("/api/throw/bizasync", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Trace-Id"));
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(40002, result.GetProperty("Code").GetInt32());
        Assert.Equal("金额必须大于 0", result.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task 未知异常_统一500()
    {
        var response = await api.Client.PostAsync("/api/throw/boomasync", content: null);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("服务器内部错误", result.GetProperty("Message").GetString());
        Assert.Equal(50000, result.GetProperty("Code").GetInt32()); // SSS+修复:50000 业务码,不泄露 ex.Message
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
