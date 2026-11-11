using System.Security.Claims;
using System.Security.Cryptography;
using NexMesh.Core;
using NexMesh.Data;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Toolbox;

namespace NexMesh.Toolbox.Tests;

/// <summary>
/// K(第三轮交叉测试):文件 Blob 存储(DbFileStorage / LocalFileStorage)数据往返与滥用面。
/// </summary>
public class KFileStorageTests : IClassFixture<KFileStorageFixture>
{
    private readonly KFileStorageFixture _fixture;

    public KFileStorageTests(KFileStorageFixture fixture) => _fixture = fixture;

    private IFileStorage NewDbStorage() => new DbFileStorage(_fixture.Db, user: null);

    private async Task RunLocalAsync(Func<IFileStorage, string, Task> body)
    {
        var dir = Path.Combine(_fixture.Root, "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(new LocalFileStorage(dir), dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private Task RunDbAsync(Func<IFileStorage, Task> body) => body(NewDbStorage());

    // ── 共享用例 ──────────────────────────────────────────────────

    private static async Task 空文件往返(IFileStorage storage)
    {
        using var upload = new MemoryStream();
        var meta = await storage.SaveAsync(upload, "空.txt", "text/plain");

        Assert.Equal(0, meta.Length);
        var opened = await storage.OpenAsync(meta.Id);
        Assert.NotNull(opened);
        using (var content = opened!.Value.Content)
        using (var reader = new BinaryReader(content))
        {
            Assert.Empty(reader.ReadBytes(16)); // 0 字节
        }
        Assert.True(await storage.DeleteAsync(meta.Id));
        Assert.Null(await storage.OpenAsync(meta.Id));
    }

    private static async Task 大文件往返(IFileStorage storage)
    {
        const int size = 5 * 1024 * 1024; // 5 MB
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        using var upload = new MemoryStream(payload);
        var meta = await storage.SaveAsync(upload, "大文件.bin", "application/octet-stream");

        Assert.Equal(size, meta.Length);
        var opened = await storage.OpenAsync(meta.Id);
        Assert.NotNull(opened);
        Assert.Equal(size, opened!.Value.Metadata.Length);
        using (var content = opened.Value.Content)
        {
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(content)); // 逐位一致(哈希)
        }
        Assert.True(await storage.DeleteAsync(meta.Id));
    }

    private static async Task 元数据往返(IFileStorage storage)
    {
        using var upload = new MemoryStream("元数据"u8.ToArray());
        const string name = "报告=最终版 v2(1)📊.pdf";
        const string contentType = "application/pdf";

        var meta = await storage.SaveAsync(upload, name, contentType);
        Assert.Equal(name, meta.Name);

        var opened = await storage.OpenAsync(meta.Id);
        Assert.NotNull(opened);
        Assert.Equal(name, opened!.Value.Metadata.Name);              // 往返后原始文件名不丢
        Assert.Equal(contentType, opened.Value.Metadata.ContentType); // ContentType 不丢
        opened.Value.Content.Dispose();

        Assert.True(await storage.DeleteAsync(meta.Id));
    }

    private static async Task 并发写同名(IFileStorage storage)
    {
        const int writers = 8;
        var tasks = Enumerable.Range(0, writers).Select(async i =>
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("写入者-" + i);
            using var upload = new MemoryStream(payload);
            var meta = await storage.SaveAsync(upload, "同名报告.xlsx", "application/vnd.ms-excel");
            return (Id: meta.Id, Payload: payload);
        }).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(writers, results.Select(r => r.Id).Distinct().Count()); // id 全局唯一,不互相覆盖
        foreach (var (id, payload) in results)
        {
            var opened = await storage.OpenAsync(id);
            Assert.NotNull(opened);
            using var content = opened!.Value.Content;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray()); // 每个写者读回自己的字节,无串写
        }
    }

    private static async Task 打开的流支持超时属性(IFileStorage storage)
    {
        using var upload = new MemoryStream("x"u8.ToArray());
        var meta = await storage.SaveAsync(upload, "t.txt");
        var opened = await storage.OpenAsync(meta.Id);
        Assert.NotNull(opened);
        byte[] bytes;
        using (var content = opened!.Value.Content)
        {
            // ASP.NET FileStreamResult 的 StreamCopyOperation 仅对 CanTimeout 流设置 ReadTimeout:
            // Db 实现为 TimeoutMemoryStream(CanTimeout=true 必须可设值),Local 为 FileStream(false 则跳过)
            if (content.CanTimeout)
            {
                content.ReadTimeout = 5000; // 不应抛 InvalidOperationException
            }
            opened.Value.Content.Position = 0;
            bytes = ReadAll(content);
        }
        Assert.Equal("x"u8.ToArray(), bytes);
        await storage.DeleteAsync(meta.Id); // 流已释放,Local 删除不撞文件占用
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ── DbFileStorage ─────────────────────────────────────────────

    [Fact]
    public Task K_Db存储_空文件往返() => RunDbAsync(空文件往返);

    [Fact]
    public Task K_Db存储_大文件5MB往返() => RunDbAsync(大文件往返);

    [Fact]
    public Task K_Db存储_名称与ContentType元数据往返() => RunDbAsync(元数据往返);

    [Fact]
    public Task K_Db存储_并发写同名8路() => RunDbAsync(并发写同名);

    [Fact]
    public Task K_Db存储_打开的流支持超时属性()
    {
        return RunDbAsync(async storage =>
        {
            using var upload = new MemoryStream("x"u8.ToArray());
            var meta = await storage.SaveAsync(upload, "t.txt");
            var opened = await storage.OpenAsync(meta.Id);
            Assert.NotNull(opened);
            using var content = opened!.Value.Content;
            Assert.True(content.CanTimeout); // TimeoutMemoryStream:FileStreamResult 依赖
            content.ReadTimeout = 5000;      // 不应抛 InvalidOperationException(Stream 基类虚属性会抛)
            await storage.DeleteAsync(meta.Id);
        });
    }

    [Fact]
    public Task K_Db存储_删除幂等与不存在id()
    {
        return RunDbAsync(async storage =>
        {
            using var upload = new MemoryStream("x"u8.ToArray());
            var meta = await storage.SaveAsync(upload, "del.txt");

            Assert.True(await storage.DeleteAsync(meta.Id));
            Assert.False(await storage.DeleteAsync(meta.Id)); // 软删后第二次删除:查不到未删记录
            Assert.Null(await storage.OpenAsync(meta.Id));    // 软删被全局过滤器隔离
            Assert.Null(await storage.OpenAsync("nonexistent"));
            Assert.False(await storage.DeleteAsync("nonexistent"));
        });
    }

    [Fact]
    public async Task K_Db存储_操作人审计写入()
    {
        var user = new KFakeUser("u-42");
        var storage = new DbFileStorage(_fixture.Db, user);
        using var upload = new MemoryStream("audit"u8.ToArray());
        await storage.SaveAsync(upload, "audit.txt");

        var entity = await _fixture.Db.Select<NexMeshFileEntity>()
            .Where(f => f.Name == "audit.txt" && !f.IsDeleted).FirstAsync();
        Assert.NotNull(entity);
        Assert.Equal("u-42", entity!.CreateUserId);
        await _fixture.Db.Update<NexMeshFileEntity>().Set(e => new NexMeshFileEntity { IsDeleted = true })
            .Where(f => f.Id == entity.Id).ExecuteAffrowsAsync();
    }

    private sealed class KFakeUser : ICurrentUser
    {
        public KFakeUser(string userId) => UserId = userId;

        public string? UserId { get; }
        public string? UserName => UserId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public string? TenantId => null;
        public bool IsAuthenticated => true;
        public string? AuthenticationType => "Fake";
        public ClaimsPrincipal? Principal => null;
        public bool IsInRole(string role) => false;
        public Claim? FindClaim(string claimType) => null;
        public string? FindClaimValue(string claimType) => null;
        public IReadOnlyList<string> FindClaimValues(string claimType) => [];
    }

    // ── LocalFileStorage ──────────────────────────────────────────

    [Fact]
    public Task K_Local存储_空文件往返() => RunLocalAsync((storage, _) => 空文件往返(storage));

    [Fact]
    public Task K_Local存储_大文件5MB往返() => RunLocalAsync((storage, _) => 大文件往返(storage));

    [Fact]
    public Task K_Local存储_名称与ContentType元数据往返() => RunLocalAsync((storage, _) => 元数据往返(storage));

    [Fact]
    public Task K_Local存储_并发写同名8路() => RunLocalAsync((storage, _) => 并发写同名(storage));

    [Fact]
    public Task K_Local存储_打开的流读回一致且不炸() => RunLocalAsync((storage, _) => 打开的流支持超时属性(storage));

    [Fact]
    public Task K_Local存储_删除幂等与不存在id()
    {
        return RunLocalAsync(async (storage, _) =>
        {
            using var upload = new MemoryStream("x"u8.ToArray());
            var meta = await storage.SaveAsync(upload, "del.txt");

            Assert.True(await storage.DeleteAsync(meta.Id));
            Assert.False(await storage.DeleteAsync(meta.Id));
            Assert.Null(await storage.OpenAsync(meta.Id));
            Assert.Null(await storage.OpenAsync("nonexistent"));
            Assert.False(await storage.DeleteAsync("nonexistent"));
        });
    }

    [Fact]
    public Task K_Local存储_路径遍历防护()
    {
        return RunLocalAsync(async (storage, dir) =>
        {
            // 在存储目录之外放置金丝雀文件:恶意 id 不得越界读/删
            var canary = Path.Combine(_fixture.Root, "k-traversal-canary.bin");
            await File.WriteAllBytesAsync(canary, new byte[] { 1, 2, 3 });
            try
            {
                Assert.Null(await storage.OpenAsync("../k-traversal-canary")); // 越界读
                Assert.False(await storage.DeleteAsync("../k-traversal-canary")); // 越界删
                Assert.Null(await storage.OpenAsync("a/b"));   // 子路径
                Assert.Null(await storage.OpenAsync(".."));
                Assert.Null(await storage.OpenAsync(""));
                Assert.Null(await storage.OpenAsync("."));
                Assert.True(File.Exists(canary));              // 金丝雀安然无恙
            }
            finally
            {
                File.Delete(canary);
            }
        });
    }
}

/// <summary>测试类夹具:唯一临时目录 + 共享 SQLite 库,类内全部测试结束后释放并清理。</summary>
public sealed class KFileStorageFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public string Root { get; }

    public IFreeSql Db { get; }

    public KFileStorageFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "nexmesh-k-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = "Data Source=" + Path.Combine(Root, "k-files.db");
            o.AutoSyncStructure = true;
        });
        _provider = services.BuildServiceProvider();
        Db = _provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
    }

    public void Dispose()
    {
        try { Db.Dispose(); } catch { /* 释放失败不阻塞清理 */ }
        _provider.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
