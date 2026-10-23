namespace NexMesh.Toolbox;

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
        var path = Path.Combine(directory, id + ".bin");
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return new FileMetadata(id, name, contentType, file.Length, DateTime.Now);
    }

    public async Task<(Stream, FileMetadata)?> OpenAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, id + ".bin");
        if (!File.Exists(path))
        {
            return null;
        }
        var info = new FileInfo(path);
        var stream = File.OpenRead(path);
        return (stream, new FileMetadata(id, id + ".bin", null, info.Length, info.CreationTime));
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, id + ".bin");
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }
        File.Delete(path);
        return Task.FromResult(true);
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
