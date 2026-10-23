namespace NexMesh.AiDemo.Entities;

using FreeSql.DataAnnotations;
using NexMesh.Data;

/// <summary>
/// 员工实体(SQLite 演示库):实现约定接口即自动接管——
/// ISoftDelete(全局过滤+软删)、IAudited(时间自动填充)、IUserAudited(操作人自动填充)。
/// Id 自增主键(非自增 long 主键由框架雪花 ID 接管)。
/// </summary>
public class Employee : ISoftDelete, IAudited, IUserAudited
{
    [Column(IsIdentity = true)]
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public bool IsDeleted { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? CreateUserId { get; set; }
    public string? UpdateUserId { get; set; }
}
