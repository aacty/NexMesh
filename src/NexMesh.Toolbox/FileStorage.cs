namespace NexMesh.Toolbox;

using System.Text.Json;
using FreeSql;
using FreeSql.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;
using NexMesh.Data;

/// <summary>文件元数据。</summary>
public sealed record FileMetadata(string Id, string Name, string? ContentType, long Length, DateTime CreateTime);

/// <summary>
/// 文件存储抽象(Db Blob / 本地磁盘 / OSS 同接口):业务只面向本接口,OSS 上线换实现零改动。
/// </summary>
public interface IFileStorage
{
    Task<FileMetadata> SaveAsync(Stream content, string name, string? contentType = null, CancellationToken ct = default);
    Task<(Stream Content, FileMetadata Metadata)?> OpenAsync(string id, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

[Table(Name = "nexmesh_files")]
public class NexMeshFileEntity : ISoftDelete, IAudited, IUserAudited
{
    public bool IsDeleted { get; set; }
    [Column(IsPrimary = true, StringLength = 64)]
    public string Id { get; set; } = "";
    [Column(StringLength = 512)]
    public string Name { get; set; } = "";
    [Column(StringLength = 256)]
    public string? ContentType { get; set; }
    public long Length { get; set; }
    [Column(StringLength = -1)]
    public byte[] Content { get; set; } = [];
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
    [Column(StringLength = 128)]
    public string? CreateUserId { get; set; }
    [Column(StringLength = 128)]
    public string? UpdateUserId { get; set; }
}

/// <summary>数据库 Blob 存储(SPD 形态:文件随事务库,可备份可审计;操作人自动审计)。</summary>
public sealed class DbFileStorage(IFreeSql db, ICurrentUser? user) : IFileStorage
{
    public async Task<FileMetadata> SaveAsync(Stream content, string name, string? contentType = null, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var entity = new NexMeshFileEntity
        {
            Id = Guid.CreateVersion7().ToString("N"),
            Name = name,
            ContentType = contentType,
            Length = ms.Length,
            Content = ms.ToArray(),
            CreateUserId = user?.UserId,
        };
        await db.Insert(entity).ExecuteAffrowsAsync(ct);
        return ToMetadata(entity);
    }

    public async Task<(Stream, FileMetadata)?> OpenAsync(string id, CancellationToken ct = default)
    {
        var entity = await db.Select<NexMeshFileEntity>().Where(f => f.Id == id).FirstAsync(ct);
        return entity is null
            ? null
            : ((Stream)new TimeoutMemoryStream(entity.Content), ToMetadata(entity));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var entity = await db.Select<NexMeshFileEntity>().Where(f => f.Id == id).FirstAsync(ct);
        if (entity is null)
        {
            return false;
        }
        entity.IsDeleted = true;
        entity.UpdateTime = DateTime.Now;
        return await db.Update<NexMeshFileEntity>().SetSource(entity).ExecuteAffrowsAsync(ct) > 0;
    }

    private static FileMetadata ToMetadata(NexMeshFileEntity e) =>
        new(e.Id, e.Name, e.ContentType, e.Length, e.CreateTime);
}

/// <summary>本地磁盘存储(开发/单机:NexMesh:Toolbox:FileStorage:Directory,缺省 ./files)。</summary>
public sealed class LocalFileStorage(string directory) : IFileStorage
{
    public async Task<FileMetadata> SaveAsync(Stream content, string name, string? contentType = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var id = Guid.CreateVersion7().ToString("N");
        await using var file = File.Create(BinPath(id));
        await content.CopyToAsync(file, ct);
        var length = file.Length;
        // 元数据伴生文件:往返后保留原始文件名/ContentType(id 本身不含用户信息)
        await File.WriteAllTextAsync(MetaPath(id), FileMeta.Encode(name, contentType), ct);
        return new FileMetadata(id, name, contentType, length, DateTime.Now);
    }

    public Task<(Stream, FileMetadata)?> OpenAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id) || !File.Exists(BinPath(id)))
        {
            return Task.FromResult<(Stream, FileMetadata)?>(null);
        }
        var info = new FileInfo(BinPath(id));
        var (name, contentType) = FileMeta.TryRead(MetaPath(id));
        var stream = File.OpenRead(BinPath(id));
        return Task.FromResult<(Stream, FileMetadata)?>(
            (stream, new FileMetadata(id, name ?? id + ".bin", contentType, info.Length, info.CreationTime)));
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id) || !File.Exists(BinPath(id)))
        {
            return Task.FromResult(false);
        }
        File.Delete(BinPath(id));
        try
        {
            File.Delete(MetaPath(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 伴生文件删除失败不影响主文件删除结果
        }
        return Task.FromResult(true);
    }

    private string BinPath(string id) => Path.Combine(directory, id + ".bin");

    private string MetaPath(string id) => Path.Combine(directory, id + ".meta");

    /// <summary>id 仅接受本实现签发的 32 位小写十六进制(Guid N 格式),防路径遍历(如 "../x" 越界读/删)。</summary>
    internal static bool IsValidId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static class FileMeta
    {
        public static string Encode(string name, string? contentType) =>
            JsonSerializer.Serialize(new Meta(name, contentType));

        public static (string? Name, string? ContentType) TryRead(string path)
        {
            try
            {
                var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path));
                return (meta?.Name, meta?.ContentType);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 伴生文件缺失/损坏(如旧版本落下的数据):退回旧行为,名 = {id}.bin
                return (null, null);
            }
        }

        private sealed record Meta(string Name, string? ContentType);
    }
}

/// <summary>支持超时的内存流:FileStreamResult 的 StreamCopyOperation 会对 CanTimeout 流设置 ReadTimeout,而 Stream 基类虚属性直接抛异常,故需完整实现。</summary>
internal sealed class TimeoutMemoryStream(byte[] content) : MemoryStream(content)
{
    private int _readTimeout = System.Threading.Timeout.Infinite;
    private int _writeTimeout = System.Threading.Timeout.Infinite;

    public override bool CanTimeout => true;
    public override int ReadTimeout { get => _readTimeout; set => _readTimeout = value; }
    public override int WriteTimeout { get => _writeTimeout; set => _writeTimeout = value; }
}
