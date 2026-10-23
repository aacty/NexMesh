using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexMesh.Core.AutoApi;
using NexMesh.Core.DependencyInjection;
using NexMesh.Toolbox;

namespace NexMesh.AiDemo.Services;

/// <summary>文件服务:IFormFile 直绑上传 → DB Blob 持久化(IFileStorage)→ 特性路由下载。</summary>
public interface IFileBiz : IAutoApi
{
    Task<string> UploadAsync(IFormFile file);

    [HttpGet("{id}")]
    Task<IActionResult> DownloadAsync(string id);
}

[ServiceRoute("api/file")]
public class FileService(IFileStorage storage) : IFileBiz, IScopedService
{
    public async Task<string> UploadAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        var meta = await storage.SaveAsync(stream, file.FileName, file.ContentType);
        return $"已存储 {meta.Name}({meta.Length} 字节,文件号 {meta.Id})";
    }

    public async Task<IActionResult> DownloadAsync(string id)
    {
        var opened = await storage.OpenAsync(id);
        if (opened is null)
        {
            return new NotFoundObjectResult($"文件 {id} 不存在");
        }
        var (content, meta) = opened.Value;
        return new FileStreamResult(content, meta.ContentType ?? "application/octet-stream")
        {
            FileDownloadName = meta.Name,
        };
    }
}
