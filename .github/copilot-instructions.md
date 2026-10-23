# NexMesh Framework - GitHub Copilot Instructions

## Context
This project uses NexMesh, a .NET 10 enterprise framework (NuGet 0.0.4, 11 packages). Follow these patterns.

## Entity Definition
- Inherit interfaces as needed: `ISoftDelete`, `IAudited` (CreateTime/UpdateTime), `IUserAudited` (CreateUserId/UpdateUserId), `IHasConcurrencyStamp`, `ITenantEntity`
- Audit fields auto-filled by framework; `long` PK auto-generates snowflake ID when 0 (identity PKs assigned by DB)

## API Pattern (Contract Endpoints)
```csharp
using Microsoft.AspNetCore.Mvc;
using NexMesh;                          // IServiceApi / [ApiContract] / PagedResult

public interface IOrderApi : IServiceApi        // route: api/{Order}/{template}
{
    [HttpGet("{id}")] Task<OrderDto?> GetAsync(long id);
    [HttpPost]       Task<OrderDto> CreateAsync(CreateOrderRequest req);
    [HttpPost("page")] Task<PagedResult<OrderDto>> GetPageAsync(OrderPageRequest req);
    [Authorize(Roles = "Admin")] Task DeleteAsync(LongIdListRequest req);
}
public class OrderService(IRepository<Order> repo) : IOrderApi, IScopedService { ... }
```
- Route convention: `api/{interface name minus I/Api}/{[Http*] template or method name minus Async}`
- `[ApiContract("orders")]` overrides the service name; default deny (unannotated = require auth), `[AllowAnonymous]` to open
- Complex-type params bind as JSON body (at most one); simple types bind from route/query
- Registration: `AddNexMeshHost`/`AddNexMeshService` scans and maps automatically; manual: `AddNexMeshContracts` + `MapNexMeshContracts`

## Key Rules
1. Business exceptions: `throw new BusinessException("msg")` / `NotFoundException(...)` — no try-catch; maps to 400/404 + envelope. `MeshException("msg", 40002, 400)` for custom codes
2. Response envelope: success `{success:true, code:0, data}` — frontends throw on `code !== 0`
3. Caching: inject `ICacheSource`, `GetOrCreateAsync(key, ttl, factory, ct)` for reads; `RemoveAsync(key)` after writes; `TryLockAsync` for distributed locks
4. DTOs: use records, never expose `IsDeleted`, `TenantId`, `ConcurrencyStamp` internals
5. Error codes: 40xxx client / 50xxx server (40001 validation, 40901 duplicate, 40902 concurrency conflict); auth failures use 401/429
6. Events: publish AFTER transaction commit
7. Secrets: config `${ENV|}` placeholders or environment variables, never in code
8. Soft delete: `repo.SoftDeleteAsync(id)` — never raw DELETE

## Middleware Order (important!)
```
UseNexMeshAuthentication   (auth handler + invalid-bearer gate; must precede the rest)
→ UseAuthorization
→ UseNexMeshService / UseNexMeshHost   (context snapshot → exception → CORS → contracts → /health → docs)
```
Optional endpoints: `MapNexMeshAuth()` (login/logout/me), `MapNexMeshApprovals()` (approval center), `MapNexMeshContractGateway()` (gateway mode).

Docs: `/openapi/v1.json` + `/scalar` (dev-only by default; `MapNexMeshDocs(force: true)` for prod).
