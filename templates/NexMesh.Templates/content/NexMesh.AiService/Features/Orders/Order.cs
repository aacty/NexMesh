using FreeSql.DataAnnotations;
using NexMesh.Data;

namespace NexMesh.AiService.Entities;

/// <summary>订单实体:实现约定接口即自动接管(软删/审计/操作人/并发/雪花)。</summary>
public class Order : ISoftDelete, IAudited, IUserAudited
{
    [Column(IsPrimary = true)]
    public long Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? CreateUserId { get; set; }
    public string? UpdateUserId { get; set; }
}
