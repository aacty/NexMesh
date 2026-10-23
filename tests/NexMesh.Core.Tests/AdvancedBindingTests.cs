namespace NexMesh.Core.Tests;

using System.Text;
using Microsoft.AspNetCore.Http;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Tests.Fixtures;

/// <summary>AutoApi 完整参数绑定:数组/集合、文件直绑、[FromForm] 对象、Body+文件混搭。</summary>
public class AdvancedBindingTests(TestApi api) : IClassFixture<TestApi>
{
    [Fact]
    public async Task 数组参数_逗号分隔Query绑定()
    {
        var response = await api.Client.PostAsync("/api/binding/sumasync?ids=1,2,3", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal("6", body);
    }

    [Fact]
    public async Task 数组参数_重复键Query绑定()
    {
        var response = await api.Client.PostAsync("/api/binding/sumasync?ids=4&ids=5", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("9", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List参数与简单参数混用()
    {
        var response = await api.Client.PostAsync("/api/binding/joinasync?parts=a,b&separator=-", content: null);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("\"a-b\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 文件直绑_IFormFile()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("hello file"), "file", "test.txt" },
        };
        var response = await api.Client.PostAsync("/api/binding/uploadasync", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("test.txt", body);
        Assert.Contains("10", body); // 文件字节数
    }

    [Fact]
    public async Task FromForm对象_字段按名绑定()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("护理部"), "department" },
            { new StringContent("3"), "level" },
        };
        var response = await api.Client.PostAsync("/api/binding/signupasync", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("护理部", body);
        Assert.Contains("3", body);
    }

    [Fact]
    public async Task Body对象与文件混搭_原fail_fast解禁()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"name":"OrderX","qty":7}""", Encoding.UTF8, "application/json"), "dto" },
            { new StringContent("sig-data"), "attachment", "sign.bin" },
        };
        var response = await api.Client.PostAsync("/api/binding/importasync", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("OrderX", body);
        Assert.Contains("sign.bin", body);
    }
}

/// <summary>完整绑定测试夹具。</summary>
[ServiceRoute("api/binding")]
public class BindingApiService : IAutoApi
{
    public Task<int> SumAsync(int[] ids) => Task.FromResult(ids.Sum());
    public Task<string> JoinAsync(List<string> parts, string separator = ",") => Task.FromResult(string.Join(separator, parts));

    public Task<string> UploadAsync(IFormFile file)
        => Task.FromResult($"{file.FileName}:{file.Length}B");

    public Task<string> SignupAsync([FromForm] SignupForm form)
        => Task.FromResult($"{form.Department}/L{form.Level}");

    public Task<string> ImportAsync(ImportDto dto, IFormFile attachment)
        => Task.FromResult($"{dto.Name}x{dto.Qty}+{attachment.FileName}");

    public record SignupForm
    {
        public string Department { get; set; } = "";
        public int Level { get; set; }
    }

    public record ImportDto
    {
        public string Name { get; set; } = "";
        public int Qty { get; set; }
    }
}
