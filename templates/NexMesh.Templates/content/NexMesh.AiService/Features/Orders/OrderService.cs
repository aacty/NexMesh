using FreeSql;
using Microsoft.AspNetCore.Mvc;
using NexMesh.AiService.Entities;
using NexMesh.Core.AutoApi;
using NexMesh.Core.DependencyInjection;
using NexMesh.Core.Models;
using NexMesh.Data;

namespace NexMesh.AiService.Features.Orders;

public record OrderDto(long Id, string Customer, decimal Amount);

public interface IOrderBiz : IAutoApi
{
    Task<long> CreateAsync(OrderDto dto);

    [HttpGet("{id}")]
    Task<OrderDto?> GetAsync(long id);

    Task<PagedData<OrderDto>> SearchAsync(string? customer, int pageIndex = 1, int pageSize = 20);
}

[ServiceRoute("api/order")]
public class OrderService(IFreeSql db, IRepository<Order> repo) : IOrderBiz, IScopedService
{
    public async Task<long> CreateAsync(OrderDto dto)
    {
        var order = await repo.AddAsync(new Order { Customer = dto.Customer, Amount = dto.Amount });
        return order.Id;
    }

    public async Task<OrderDto?> GetAsync(long id)
    {
        var order = await db.Select<Order>().Where(o => o.Id == id).FirstAsync();
        return order is null ? null : new OrderDto(order.Id, order.Customer, order.Amount);
    }

    public async Task<PagedData<OrderDto>> SearchAsync(string? customer, int pageIndex = 1, int pageSize = 20)
    {
        var query = db.Select<Order>()
            .WhereIf(!string.IsNullOrWhiteSpace(customer), o => o.Customer.Contains(customer!.Trim()));
        var total = await query.CountAsync();
        var items = await query.OrderBy(o => o.Id).Page(pageIndex, pageSize).ToListAsync();
        return new PagedData<OrderDto>
        {
            Total = total,
            Items = items.Select(o => new OrderDto(o.Id, o.Customer, o.Amount)).ToList(),
        };
    }
}
