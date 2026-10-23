using NexMesh.Core;
using NexMesh.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Toolbox;

namespace NexMesh.Toolbox.Tests;

public class ToolboxTests
{
    // ── 数据脱敏 ──────────────────────────────────────────────

    [Theory]
    [InlineData("13812345678", "138****5678")]
    [InlineData("abc", "abc")] // 非手机号原样返回
    public void 手机号脱敏(string input, string expected) =>
        Assert.Equal(expected, Masking.Phone(input));

    [Theory]
    [InlineData("alice@example.com", "a**@example.com")]
    [InlineData("a@x.com", "*@x.com")]
    [InlineData("not-an-email", "not-an-email")]
    public void 邮箱脱敏(string input, string expected) =>
        Assert.Equal(expected, Masking.Email(input));

    [Fact]
    public void 身份证与姓名脱敏()
    {
        Assert.Equal("1101**********1234", Masking.IdCard("110101199001011234"));
        Assert.Equal("张**", Masking.Name("张三丰"));
    }

    // ── Excel 导入导出 ─────────────────────────────────────────

    private sealed record EmployeeRow
    {
        public string Name { get; set; } = "";
        public string Department { get; set; } = "";
    }

    [Fact]
    public async Task Excel_内存往返无损()
    {
        var rows = new List<EmployeeRow>
        {
            new() { Name = "张三", Department = "护理部" },
            new() { Name = "李四", Department = "信息科" },
        };
        using var stream = new MemoryStream();
        await NexMeshExcel.ExportAsync(stream, rows);

        var imported = await NexMeshExcel.ImportAsync<EmployeeRow>(stream);

        Assert.Equal(2, imported.Count);
        Assert.Equal("张三", imported[0].Name);
        Assert.Equal("信息科", imported[1].Department);
    }

    // ── 文件存储(双实现同语义) ─────────────────────────────────

    private static async Task Roundtrip(IFileStorage storage)
    {
        var payload = "hello nexmesh file storage"u8.ToArray();
        using var upload = new MemoryStream(payload);
        var meta = await storage.SaveAsync(upload, "报告.pdf", "application/pdf");

        Assert.NotEmpty(meta.Id);
        Assert.Equal("报告.pdf", meta.Name);
        Assert.Equal(payload.Length, meta.Length);

        var opened = await storage.OpenAsync(meta.Id);
        Assert.NotNull(opened);
        using (var reader = new BinaryReader(opened!.Value.Content))
        {
            Assert.Equal(payload, reader.ReadBytes((int)opened.Value.Metadata.Length));
        } // 读流先释放,再删

        Assert.True(await storage.DeleteAsync(meta.Id));
        Assert.Null(await storage.OpenAsync(meta.Id));
    }

    [Fact]
    public async Task 本地磁盘存储_存取删往返()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexmesh-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Roundtrip(new LocalFileStorage(dir)); // Roundtrip 末尾已物理删除
            Assert.Empty(Directory.GetFiles(dir, "*"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 数据库Blob存储_存取删往返()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:db{Guid.NewGuid():N}?mode=memory&cache=shared";
            o.AutoSyncStructure = true;
        });
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();

        await Roundtrip(new DbFileStorage(db, user: null));
    }
}
