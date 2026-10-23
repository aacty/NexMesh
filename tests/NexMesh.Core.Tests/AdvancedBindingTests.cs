namespace NexMesh.Core.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NexMesh;

/// <summary>契约端点完整参数绑定:数组/集合、文件直绑、[FromForm] 对象、Body+文件混搭。</summary>
public class AdvancedBindingTests(TestApi api) : IClassFixture<TestApi>
{
    private static async Task<JsonElement> DataOf(System.Net.Http.HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("data");
    }

    [Fact]
    public async Task 数组参数_逗号分隔Query绑定()
    {
        var response = await api.Client.PostAsync("/api/Binding/Sum?ids=1,2,3", content: null);
        Assert.Equal(6, (await DataOf(response)).GetInt32());
    }

    [Fact]
    public async Task 数组参数_重复键Query绑定()
    {
        var response = await api.Client.PostAsync("/api/Binding/Sum?ids=4&ids=5", content: null);
        Assert.Equal(9, (await DataOf(response)).GetInt32());
    }

    [Fact]
    public async Task List参数与简单参数混用()
    {
        var response = await api.Client.PostAsync("/api/Binding/Join?parts=a,b&separator=-", content: null);
        Assert.Equal("a-b", (await DataOf(response)).GetString());
    }

    [Fact]
    public async Task 文件直绑_IFormFile()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("hello file"), "file", "test.txt" },
        };
        var response = await api.Client.PostAsync("/api/Binding/Upload", content);
        var data = (await DataOf(response)).GetString();
        Assert.Contains("test.txt", data);
        Assert.Contains("10", data); // 文件字节数
    }

    [Fact]
    public async Task FromForm对象_字段按名绑定()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("护理部"), "department" },
            { new StringContent("3"), "level" },
        };
        var response = await api.Client.PostAsync("/api/Binding/Signup", content);
        Assert.Equal("护理部/L3", (await DataOf(response)).GetString());
    }

    [Fact]
    public async Task Body对象与文件混搭_原fail_fast解禁()
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"name":"OrderX","qty":7}""", Encoding.UTF8, "application/json"), "dto" },
            { new StringContent("sig-data"), "attachment", "sign.bin" },
        };
        var response = await api.Client.PostAsync("/api/Binding/Import", content);
        var data = (await DataOf(response)).GetString();
        Assert.Equal("OrderXx7+sign.bin", data);
    }
}

/// <summary>完整绑定测试契约。</summary>
public interface IBindingApi : IServiceApi
{
    Task<int> SumAsync(int[] ids);

    Task<string> JoinAsync(List<string> parts, string separator = ",");

    Task<string> UploadAsync(IFormFile file);

    Task<string> SignupAsync([FromForm] SignupForm form);

    Task<string> ImportAsync(ImportDto dto, IFormFile attachment);
}

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

public class BindingApiService : IBindingApi
{
    public Task<int> SumAsync(int[] ids) => Task.FromResult(ids.Sum());

    public Task<string> JoinAsync(List<string> parts, string separator = ",")
        => Task.FromResult(string.Join(separator, parts));

    public Task<string> UploadAsync(IFormFile file)
        => Task.FromResult($"{file.FileName}:{file.Length}B");

    public Task<string> SignupAsync(SignupForm form)
        => Task.FromResult($"{form.Department}/L{form.Level}");

    public Task<string> ImportAsync(ImportDto dto, IFormFile attachment)
        => Task.FromResult($"{dto.Name}x{dto.Qty}+{attachment.FileName}");
}
