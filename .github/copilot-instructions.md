# NexMesh Framework - GitHub Copilot Instructions

## Context
This project uses NexMesh, a .NET 10 enterprise framework. Follow these patterns.

## Entity Definition
- Inherit interfaces as needed: `ISoftDelete`, `IAudited`, `IHasConcurrencyStamp`, `ITenantEntity`
- `Guid` or `long` primary key (long auto-generates snowflake ID when 0)
- All audit fields auto-filled by framework

## API Pattern (AutoApi)
```csharp
public interface IOrderApi : IAutoApi
{
    [HttpGet("{id}")] Task<OrderDto?> FindAsync(long id);
    [HttpPost] Task<OrderDto> CreateAsync(CreateOrderRequest req);
}
public class OrderApi(IRepository<Order> repo) : IOrderApi { ... }
```
Register: `builder.Services.AddNexMeshAutoApi();` + `app.MapNexMeshAutoApi();`

## Key Rules
1. Business exceptions: `throw new MeshException("msg", errorCode: 40401, statusCode: 404);` — no try-catch
2. Pagination: `.OrderByWhitelist(sortBy, whitelist: [...]).ToPagedResultAsync(page, size)` — never raw Skip/Take
3. Caching: `cache.Get().GetOrCreateAsync(key, ttl, factory, ct)` for reads; `cache.Get().RemoveAsync(key)` after writes
4. DTOs: use records, never expose `IsDeleted`, `TenantId`, `ConcurrencyStamp` internals
5. Error codes: 400xx=validation, 404xx=not found, 40901=duplicate, 40902=concurrency conflict
6. Events: publish AFTER transaction commit
7. Secrets: environment variables only (`NEXMESH_JWT_KEY`, `NEXMESH_DB`)
8. Soft delete: `repo.SoftDeleteAsync(entity)` — never `DELETE` SQL

## Middleware Order (important!)
```
UseNexMeshTraceId → UseNexMeshApiVersion → UseNexMeshIdempotency → UseNexMeshAudit
→ UseNexMeshExceptionHandling → UseAuthentication → UseAuthorization → UseRateLimiter
→ MapNexMeshAutoApi → MapDefaultEndpoints → MapNexMeshDocs
```
