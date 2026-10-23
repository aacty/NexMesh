namespace NexMesh.AiDemo.Services;

using FreeSql;
using Microsoft.AspNetCore.Mvc;
using NexMesh.AiDemo.Entities;
using NexMesh.Core.AutoApi;
using NexMesh.Core.DependencyInjection;
using NexMesh.Core.Models;
using NexMesh.Data;

public record EmployeeDto(long Id, string Name, string Department);

/// <summary>
/// 员工管理契约:几千人规模的完整 CRUD。注意与 P1 版的差异——
/// 软删过滤/审计时间/操作人全部由数据层约定自动接管,业务代码只剩纯业务判断。
/// </summary>
public interface IEmployeeBiz : IAutoApi
{
    Task<long> CreateAsync(EmployeeDto dto);

    [HttpGet("{id}")]
    Task<EmployeeDto?> GetAsync(long id);

    [HttpPut("{id}")]
    Task<bool> UpdateAsync(long id, EmployeeDto dto);

    [HttpDelete("{id}")]
    Task<bool> DeleteAsync(long id);

    Task<PagedData<EmployeeDto>> SearchAsync(string? keyWords, string? department, int pageIndex = 1, int pageSize = 20);

    Task<long> GetCountAsync();
}

/// <summary>
/// 写路径走 IRepository(审计/操作人/软删自动),读路径直接 IFreeSql(全局软删过滤生效)。
/// </summary>
[ServiceRoute("api/employee")]
public class EmployeeService(IFreeSql db, IRepository<Employee> repo) : IEmployeeBiz, IScopedService
{
    public async Task<long> CreateAsync(EmployeeDto dto)
    {
        var entity = await repo.AddAsync(new Employee { Name = dto.Name, Department = dto.Department });
        return entity.Id;
    }

    public async Task<EmployeeDto?> GetAsync(long id)
    {
        var entity = await db.Select<Employee>().Where(e => e.Id == id).FirstAsync();
        return entity is null ? null : ToDto(entity);
    }

    public async Task<bool> UpdateAsync(long id, EmployeeDto dto)
    {
        var entity = await db.Select<Employee>().Where(e => e.Id == id).FirstAsync();
        if (entity is null)
        {
            return false;
        }
        entity.Name = dto.Name;
        entity.Department = dto.Department;
        await repo.UpdateAsync(entity);
        return true;
    }

    public Task<bool> DeleteAsync(long id) => repo.SoftDeleteAsync(id);

    public async Task<PagedData<EmployeeDto>> SearchAsync(string? keyWords, string? department, int pageIndex = 1, int pageSize = 20)
    {
        var query = db.Select<Employee>()
            .WhereIf(!string.IsNullOrWhiteSpace(keyWords), e => e.Name.Contains(keyWords!.Trim()))
            .WhereIf(!string.IsNullOrWhiteSpace(department), e => e.Department == department!.Trim());

        var total = await query.CountAsync();
        var items = await query.OrderBy(e => e.Id).Page(pageIndex, pageSize).ToListAsync();

        return new PagedData<EmployeeDto>
        {
            Total = total,
            Items = items.Select(ToDto).ToList(),
        };
    }

    public Task<long> GetCountAsync() => db.Select<Employee>().CountAsync();

    private static EmployeeDto ToDto(Employee e) => new(e.Id, e.Name, e.Department);
}
