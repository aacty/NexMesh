# NexMesh 25 步完整教程

.NET 10 Web API 开发框架。帮你处理统一响应格式、全局异常、数据库 CRUD、分页、日志、健康检查这些事,你只写业务代码。

---

## 目录

1. [快速开始](#快速开始)
2. [跑起来](#第一步跑起来)
3. [定义实体](#第二步定义实体)
4. [增删改查](#第三步增删改查)
5. [暴露 API(四种方式)](#第四步暴露-api)
6. [分页查询](#第五步分页查询)
7. [缓存](#第六步加缓存)
8. [认证(JWT)](#第七步加认证)
9. [多租户](#第八步多租户)
10. [事件](#第九步事件)
11. [审计日志](#第十步审计日志)
12. [幂等防重](#第十一步幂等防重)
13. [限流](#第十二步限流)
14. [乐观并发](#第十三步乐观并发)
15. [数据库迁移](#第十四步数据库迁移)
16. [后台任务](#第十五步后台任务)
17. [对象映射](#第十六步对象映射)
18. [HTTP 弹性](#第十七步http-弹性)
19. [分布式 ID](#第十八步分布式-id)
20. [脱敏](#第十九步脱敏)
21. [Excel 导入导出](#第二十步excel-导入导出)
22. [API 版本控制](#第二十一步api-版本控制)
23. [运行时诊断](#第二十二步运行时诊断)
24. [日志](#第二十三步日志)
25. [链路追踪](#第二十四步链路追踪)
26. [API 文档](#第二十五步api-文档)
27. [部署(六种方式)](#部署)
28. [运维端点](#运维端点)
29. [出错了怎么查](#出错了怎么查)
30. [超大型项目实战指南](#超大型项目实战指南)

---

## 快速开始

```bash
# 1. 创建项目
dotnet new webapi -n MyApp
cd MyApp

# 2. 装 NexMesh(包还在内部源,先打包)
#    在 NexMesh 仓库根目录:
dotnet pack src/NexMesh.Core -o artifacts/nupkgs
dotnet pack src/NexMesh.Data.FreeSql -o artifacts/nupkgs
dotnet pack src/NexMesh.Cache.FreeRedis -o artifacts/nupkgs
dotnet pack src/NexMesh.Auth -o artifacts/nupkgs
dotnet pack src/NexMesh.Web.Scalar -o artifacts/nupkgs

# 3. 在 MyApp 目录创建 NuGet.config 指向本地包
#    (内容见 templates/NexMesh.Templates/README.md)

# 4. 装包
dotnet add package NexMesh.Core
dotnet add package NexMesh.Data.FreeSql
dotnet add package NexMesh.Cache.FreeRedis
dotnet add package NexMesh.Auth
dotnet add package NexMesh.Web.Scalar
```

---

## 第一步:跑起来

替换 Program.cs:

```csharp
using FreeSql;                        // DataType 枚举在这里
using NexMesh.Core;
using NexMesh.Data.FreeSql;

var builder = WebApplication.CreateBuilder(args);

// 注册 NexMesh 核心能力
builder.Services.AddNexMesh();

// 注册数据库(这里用 SQLite 演示,生产换 MySQL/PostgreSQL)
builder.Services.AddNexMeshDb("main", o =>
{
    o.DataType = DataType.Sqlite;
    o.MasterConnectionString = "Data Source=app.db";
    o.UseAutoSyncStructure = builder.Environment.IsDevelopment();
    // 开发环境自动建表;生产环境用 SQL 迁移(后面讲)
});

var app = builder.Build();

// 中间件(顺序重要)
app.UseNexMeshTraceId();              // 每个响应带 X-Trace-Id 头,排障用
app.UseNexMeshExceptionHandling();    // 未捕获的异常自动转 JSON
app.MapDefaultEndpoints();            // /health /health/live /health/ready

// 写个测试端点
app.MapGet("/hello", () => "ok");

app.Run();
```

运行后:
- 打开 `/health` → 看到 `{"status":"Healthy"}` 说明服务正常
- 打开 `/hello` → 返回 `ok`

---

## 第二步:定义实体

实体就是一个 C# 类,继承几个接口告诉框架你需要什么能力:

```csharp
using NexMesh.Core;

public class Order : ISoftDelete, IAudited
{
    public Guid Id { get; set; }              // 主键(Guid 或 long 都行)
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }

    // ISoftDelete: 删除时标记 IsDeleted=true(不是真删),查询自动过滤
    public bool IsDeleted { get; set; }

    // IAudited: 创建/更新时间、操作人自动填,不用手动赋值
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
```

### 接口一览(按需继承)

| 接口 | 效果 | 什么时候用 |
|---|---|---|
| `ISoftDelete` | 删除标记,查询自动过滤 | 需要回收站/审计追踪 |
| `IAudited` | 创建/更新时间、操作人自动填 | 需要知道谁在什么时候改的 |
| `IHasConcurrencyStamp` | 两人同时改同一条,后提交的收到 409 | 表单提交/审批流 |
| `ITenantEntity` | 多租户数据隔离 | SaaS 多租户系统 |

### 全部接口一起用

```csharp
public class Order : ISoftDelete, IAudited, IHasConcurrencyStamp, ITenantEntity
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }

    // ISoftDelete
    public bool IsDeleted { get; set; }

    // IAudited
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    // IHasConcurrencyStamp(并发保护)
    public string ConcurrencyStamp { get; set; } = "";

    // ITenantEntity(多租户)
    public string? TenantId { get; set; }
}
```

### long 主键自动生成雪花 ID

```csharp
// Id 不赋值(=0)时,框架自动生成全局唯一的雪花 ID
public class Category : ISoftDelete
{
    public long Id { get; set; }    // 0 → 自动填雪花 ID(如 784251234567890)
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
}

// 用的时候不需要手动赋值:
var category = await repo.AddAsync(new Category { Name = "laptop" });
// category.Id 已经是雪花 ID 了,比如 784251234567890
```

---

## 第三步:增删改查

在 Service 或端点里注入 `IRepository<T>`:

```csharp
using NexMesh.Data.FreeSql;

public class OrderService(IRepository<Order> repo)
{
    // 新增:CreatedAt/CreatedBy 自动填;ConcurrencyStamp 在 InsertOrUpdateAsync 插入时初始化
    public async Task<Order> Create(string name, decimal amount)
    {
        return await repo.AddAsync(new Order
        {
            CustomerName = name,
            Amount = amount,
        });
    }

    // 按主键查(软删除的自动排除)
    public async Task<Order?> Get(Guid id)
    {
        return await repo.FindAsync(id);
    }

    // 按条件查单条
    public async Task<Order?> GetByName(string name)
    {
        return await repo.GetAsync(o => o.CustomerName == name);
    }

    // 查列表
    public async Task<List<Order>> List()
    {
        return await repo.ListAsync();
    }

    // 按条件查列表
    public async Task<List<Order>> ListByAmount(decimal min)
    {
        return await repo.ListAsync(o => o.Amount >= min);
    }

    // 计数
    public async Task<long> Count()
    {
        return await repo.CountAsync();
    }

    // 更新(如果实体有 IHasConcurrencyStamp,自动检查并发冲突)
    public async Task Update(Order order)
    {
        await repo.UpdateAsync(order);
    }

    // 批量更新
    public async Task UpdateMany(List<Order> orders)
    {
        await repo.UpdateManyAsync(orders);
    }

    // Upsert(不存在就插入,存在就更新)
    public async Task<int> Upsert(Order order)
    {
        return await repo.InsertOrUpdateAsync(order);
    }

    // 软删除(标记 IsDeleted=true,不是物理删除)
    public async Task Delete(Order order)
    {
        await repo.SoftDeleteAsync(order);
    }
}
```

### 复杂查询(直接用 IFreeSql)

仓储只做简单 CRUD,复杂查询直接用 `IDbFactory`:

```csharp
public class ReportService(IDbFactory db)
{
    // Join 查询
    public async Task<List<OrderReport>> GetReport()
    {
        return await db.Get().Select<Order>()
            .Where(o => o.Amount > 100 && !o.IsDeleted)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    // 聚合
    public async Task<decimal> GetTotalAmount()
    {
        return await db.Get().Select<Order>()
            .Where(o => !o.IsDeleted)
            .SumAsync(o => o.Amount);
    }
}
```

### 查已删除的数据(回收站功能)

```csharp
// 默认查不到已删除的(软删除过滤生效)
var active = await repo.FindAsync(id);    // null(已删除)

// 绕过过滤器查已删除的:
var deleted = await db.Get().Select<Order>()
    .DisableGlobalFilter(NexMeshDbDefaults.SoftDeleteFilterName)
    .Where(o => o.Id == id)
    .FirstAsync();
```

---

## 第四步:暴露 API

NexMesh 默认搭配 **FastEndpoints**(脚手架模板使用)和 **自动 API**(写接口+实现类)两种方式,**可以混用**。中间件和数据层在所有写法下完全通用。

### 方式 A:FastEndpoints(默认,脚手架模板使用)

```csharp
using FastEndpoints;
using NexMesh.Core;
using NexMesh.Data.FreeSql;

public class CreateOrder(IRepository<Order> repo)
    : Endpoint<CreateOrderRequest, ApiResult<OrderDto>>
{
    public override void Configure()
    {
        Post("/api/orders");
        AllowAnonymous();       // 改成 Roles("admin") 就需要角色
        Summary(s => s.Summary = "创建订单");
    }

    public override async Task HandleAsync(CreateOrderRequest req, CancellationToken ct)
    {
        if (req.Amount <= 0)
            throw new MeshException("金额必须大于 0", errorCode: 40002);

        var order = await repo.AddAsync(new Order
        {
            CustomerName = req.CustomerName,
            Amount = req.Amount,
        }, ct);

        await HttpContext.Response.SendAsync(
            ApiResult<OrderDto>.Ok(new OrderDto(order.Id, order.CustomerName, order.Amount)),
            201);
    }
}

// Program.cs:
builder.Services.AddFastEndpoints();
// ...
app.UseFastEndpoints();
```

### 方式 B:自动 API(最少代码,与 FastEndpoints 可混用)

写接口 + 实现类,不用继承任何父类。同一个项目里可以部分模块用 FastEndpoints,部分用自动 API:

```csharp
using Microsoft.AspNetCore.Mvc;
using NexMesh.Core;
using NexMesh.Data.FreeSql;

// 1. 定义接口:用标准 [HttpGet] [HttpPost] 注解
public interface IOrderApi : IAutoApi
{
    [HttpGet("{id}")]
    Task<OrderDto?> FindAsync(long id);              // GET /api/orders/5

    [HttpGet]
    Task<List<OrderDto>> ListAsync();                // GET /api/orders

    [HttpPost]
    Task<OrderDto> CreateAsync(CreateOrderRequest req); // POST /api/orders

    [HttpPut("{id}")]
    Task<OrderDto> UpdateAsync(long id, UpdateOrderRequest req); // PUT /api/orders/5

    [HttpDelete("{id}")]
    Task DeleteAsync(long id);                       // DELETE /api/orders/5
}

// 2. 写实现类:普通类,构造函数注入仓储
public class OrderApi(IRepository<Order> repo) : IOrderApi
{
    public async Task<OrderDto?> FindAsync(long id)
    {
        var order = await repo.FindAsync(id);
        if (order is null) return null;
        return new OrderDto(order.Id, order.CustomerName, order.Amount);
    }

    public async Task<List<OrderDto>> ListAsync()
    {
        var orders = await repo.ListAsync();
        return orders.Select(o => new OrderDto(o.Id, o.CustomerName, o.Amount)).ToList();
    }

    public async Task<OrderDto> CreateAsync(CreateOrderRequest req)
    {
        if (req.Amount <= 0)
            throw new MeshException("金额必须大于 0", errorCode: 40002);

        var order = await repo.AddAsync(new Order
        {
            CustomerName = req.CustomerName,
            Amount = req.Amount,
        });
        return new OrderDto(order.Id, order.CustomerName, order.Amount);
    }

    public async Task<OrderDto> UpdateAsync(long id, UpdateOrderRequest req)
    {
        var order = await repo.FindAsync(id)
            ?? throw new MeshException("订单不存在", errorCode: 40401, statusCode: 404);
        order.CustomerName = req.CustomerName;
        order.Amount = req.Amount;
        await repo.UpdateAsync(order);
        return new OrderDto(order.Id, order.CustomerName, order.Amount);
    }

    public async Task DeleteAsync(long id)
    {
        var order = await repo.FindAsync(id)
            ?? throw new MeshException("订单不存在", errorCode: 40401, statusCode: 404);
        await repo.SoftDeleteAsync(order);
    }
}

// DTO 定义
public record CreateOrderRequest(string CustomerName, decimal Amount);
public record UpdateOrderRequest(string CustomerName, decimal Amount);
public record OrderDto(Guid Id, string CustomerName, decimal Amount);

// 3. Program.cs 加两行:
builder.Services.AddNexMeshAutoApi();
// ...
app.MapNexMeshAutoApi();
```

**注解优先;没写注解默认 GET。** 用的是 ASP.NET Core 标准 `[HttpGet]` `[HttpPost]`,不用学新东西。

**参数绑定规则:**

| 参数类型 | 来源 | 示例 |
|---|---|---|
| long / string / Guid | 路由段 `{id}` 或查询参数 `?name=xxx` | `FindAsync(long id)` ← `/api/orders/5` |
| class / record | JSON Body | `CreateAsync(CreateOrderRequest req)` ← `{"customerName":"张三","amount":99}` |
| CancellationToken | 自动注入 | 不需要手动传 |

**响应格式(自动包装):**

```json
// 成功
{"data": {"id": 1, "customerName": "张三", "amount": 99}, "success": true}

// 失败(抛 MeshException 自动转)
{"success": false, "code": 40002, "message": "金额必须大于 0", "traceId": "00-abc..."}
```

### 方式 C:Minimal API(原生写法)

什么都不装,直接 `app.MapGet`:

```csharp
app.MapGet("/api/orders/{id:guid}", async (Guid id, IRepository<Order> repo) =>
{
    var order = await repo.FindAsync(id)
        ?? throw new MeshException("订单不存在", errorCode: 40401, statusCode: 404);
    return Results.Ok(ApiResult.Ok(order));
});

app.MapPost("/api/orders", async (CreateOrderRequest req, IRepository<Order> repo) =>
{
    var order = await repo.AddAsync(new Order
    {
        CustomerName = req.CustomerName,
        Amount = req.Amount,
    });
    return Results.Created($"/api/orders/{order.Id}", ApiResult.Ok(order));
});
```

### 方式 D:MVC Controller(传统写法)

已有 Controller 项目迁移:

```csharp
// Program.cs 加两行:
builder.Services.AddControllers();
// ...
app.MapControllers();

// Controller 正常写:
[ApiController]
[Route("api/[controller]")]
public class OrdersController(IRepository<Order> repo) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var order = await repo.FindAsync(id)
            ?? throw new MeshException("订单不存在", errorCode: 40401, statusCode: 404);
        return Ok(ApiResult.Ok(order));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderRequest req)
    {
        var order = await repo.AddAsync(new Order
        {
            CustomerName = req.CustomerName,
            Amount = req.Amount,
        });
        return CreatedAtAction(nameof(Get), new { id = order.Id },
            ApiResult.Ok(order));
    }
}
```

---

## 第五步:分页查询

```csharp
using NexMesh.Core;
using NexMesh.Data.FreeSql;

public class OrderListService(IDbFactory db)
{
    public async Task<PagedResult<Order>> GetOrders(int pageIndex, int pageSize, string? sortBy)
    {
        return await db.Get().Select<Order>()
            .Where(o => o.Amount > 100)                                    // 筛选
            .OrderByWhitelist(sortBy, whitelist: ["CreatedAt", "Amount"])  // 排序白名单(防注入)
            .ToPagedResultAsync(pageIndex, pageSize);
    }
}
```

返回的 `PagedResult<T>`:

```json
{
  "items": [...],          // 当前页数据
  "totalCount": 100,       // 总条数
  "pageIndex": 1,          // 当前页
  "pageSize": 20,          // 每页条数
  "totalPages": 5          // 总页数
}
```

**排序白名单:** `sortBy` 只能传 `"CreatedAt"` 或 `"Amount"`,传其他值返回 400(40010)。这样前端不能传任意字符串做排序,防止 SQL 注入。

---

## 第六步:加缓存

```csharp
using NexMesh.Cache.FreeRedis;

// 注册(没配 Redis 连接串自动用内存缓存,本地开发不用装 Redis)
builder.Services.AddNexMeshCache(builder.Configuration);

// 配置 Redis(可选,生产环境):
// appsettings.json:
// { "NexMesh": { "Cache": { "Sources": { "main": { "ConnectionString": "localhost:6379" } } } } }
```

使用:

```csharp
public class ProductService(ICacheFactory cacheFactory)
{
    // 缓存有 → 直接返回;缓存没有 → 查库后存入缓存
    // 同一个 key 并发只放一个请求去查库(防击穿)
    public async Task<ProductDto?> GetProduct(long id, CancellationToken ct)
    {
        return await cacheFactory.Get().GetOrCreateAsync(
            $"product:{id}",                    // 缓存 key
            TimeSpan.FromMinutes(5),             // 5 分钟过期
            async token => await LoadFromDb(id, token),  // 缓存没有时执行
            ct);
    }

    // 写操作后记得清缓存
    public async Task UpdateProduct(Product product, CancellationToken ct)
    {
        await _repo.UpdateAsync(product, ct);
        await cacheFactory.Get().RemoveAsync($"product:{product.Id}", ct);  // 别忘了清
    }
}
```

**Redis 挂了会怎样?** 自动降级到内存缓存,60 秒后自动重试 Redis。不需要写任何降级代码。

**分布式锁:**

```csharp
// 两个请求同时到,只有一个能拿到锁
var gate = await cacheFactory.Get().TryLockAsync($"lock:order:{orderId}", TimeSpan.FromSeconds(30));

if (gate is null)
{
    // 没拿到锁,说明另一个请求正在处理
    return "处理中,请稍后";
}

try
{
    // 拿到锁,安全操作
    await ProcessOrder(orderId);
}
finally
{
    await gate.DisposeAsync();    // 释放锁
}
```

---

## 第七步:加认证

### JWT 认证

```csharp
using NexMesh.Auth;

// 注册
builder.Services.AddNexMeshJwt(builder.Configuration);

// appsettings.json:
// { "NexMesh": { "Auth": { "Jwt": { "Key": "至少32个字符的密钥...", "Issuer": "myapp" } } } }
```

签发 token:

```csharp
public class LoginService(ITokenService tokenService)
{
    public string Login(string username, string password)
    {
        // 验证密码(省略)
        return tokenService.Issue(new Dictionary<string, object>
        {
            ["sub"] = "user-123",       // 用户 ID
            ["name"] = username,         // 用户名
            ["role"] = "admin",          // 角色
        });
    }
}
```

获取当前登录用户:

```csharp
public class ProfileService(ICurrentUser currentUser)
{
    public string? UserId => currentUser.Id;         // "user-123"
    public string? UserName => currentUser.UserName;  // "张三"
    public bool IsAdmin => currentUser.Roles.Contains("admin");
}
```

保护端点:

```csharp
// FastEndpoints:
public override void Configure()
{
    Post("/api/orders");
    Roles("admin");        // 需要 admin 角色
}

// Minimal API:
app.MapGet("/api/profile", (ICurrentUser user) => user.UserName)
    .RequireAuthorization();
```

### ApiKey 认证(服务间调用)

```csharp
// 配置(具名模式,每个调用方独立 key):
// { "NexMesh": { "Auth": { "ApiKeyMap": { "billing-service": "its-secret-key" } } } }

builder.Services.AddNexMeshApiKey(builder.Configuration);

// 调用时带请求头:
// X-Api-Key: its-secret-key
// 框架自动验证并注入身份(Claim: NameIdentifier = "billing-service")
```

---

## 第八步:多租户

```csharp
// 1. 实体实现 ITenantEntity
public class Order : ITenantEntity
{
    public string? TenantId { get; set; }
    // ... 其他字段
}

// 2. 开启租户过滤
builder.Services.AddNexMeshDb("main", o =>
{
    o.DataType = DataType.MySql;
    o.MasterConnectionString = "...";
    o.EnableTenantFilter = true;     // ← 加这一行
});

// 3. 设置当前租户(查询自动限定)
using var _ = CurrentTenant.Change("tenant-001");
var orders = await repo.ListAsync();
// 只能看到 TenantId == "tenant-001" 的订单

// 4. 宿主模式(看到全部数据,不过滤)
using var __ = CurrentTenant.Change(null);
var allOrders = await repo.ListAsync();
// 看到所有租户的订单
```

通常在中间件里根据 JWT 的 `tenant_id` claim 设置:

```csharp
app.Use(async (ctx, next) =>
{
    var tenantId = ctx.User.FindFirst("tenant_id")?.Value;
    using var _ = CurrentTenant.Change(tenantId);
    await next();
});
```

---

## 第九步:事件

```csharp
using NexMesh.Events;

// 1. 定义事件(用 record)
public record OrderCreated(Guid OrderId, string CustomerName, decimal Amount);

// 2. 定义处理器
public class SendEmailOnCreated(ILogger<SendEmailOnCreated> logger)
    : INexMeshEventHandler<OrderCreated>
{
    public Task HandleAsync(OrderCreated e, CancellationToken ct)
    {
        logger.LogInformation("订单 {OrderId} 创建,发邮件给 {Customer}", e.OrderId, e.CustomerName);
        return Task.CompletedTask;
    }
}

public class UpdateInventoryOnCreated(IRepository<Stock> stockRepo)
    : INexMeshEventHandler<OrderCreated>
{
    public async Task HandleAsync(OrderCreated e, CancellationToken ct)
    {
        // 扣库存
    }
}

// 3. 注册(默认进程内,不装 MQ 也能跑)
builder.Services.AddNexMeshEvents(builder.Configuration);
builder.Services.AddNexMeshEventHandler<OrderCreated, SendEmailOnCreated>();
builder.Services.AddNexMeshEventHandler<OrderCreated, UpdateInventoryOnCreated>();

// 4. 发布
public class OrderService(INexMeshEventPublisher events)
{
    public async Task Create(Order order)
    {
        // ... 创建订单
        await events.PublishAsync(new OrderCreated(order.Id, order.CustomerName, order.Amount));
        // 两个处理器都会被调用,单个失败不影响其他
    }
}
```

**切换到 RabbitMQ(生产环境):**

```json
// appsettings.json:
{ "NexMesh": { "Events": {
    "Transport": "RabbitMQ",
    "Storage": "PostgreSql",
    "RabbitMqHostName": "mq.mycompany.com",
    "PostgreSqlConnectionString": "Host=...;Database=..."
} } }
```

---

## 第十步:审计日志

```csharp
// 启用(每个请求自动记录)
app.UseNexMeshAudit();
```

日志输出:

```
审计 GET /api/orders → 200 耗时 5.2ms 用户:user-123 租户:tenant-001 TraceId:00-abc...
审计 POST /api/orders → 201 耗时 45.8ms 用户:user-123 租户:tenant-001 TraceId:00-def...
慢请求 GET /api/report → 200 耗时 2500ms TraceId:00-ghi...    ← 超过 1 秒自动升级 Warning
```

**需要落库?** 实现 `IAuditLogStore`:

```csharp
public class DbAuditStore(IRepository<AuditLog> repo) : IAuditLogStore
{
    public async Task WriteAsync(AuditLogEntry entry, CancellationToken ct)
    {
        await repo.AddAsync(new AuditLog
        {
            Method = entry.Method,       // "POST"
            Path = entry.Path,           // "/api/orders"
            StatusCode = entry.StatusCode, // 201
            ElapsedMs = entry.ElapsedMs, // 45.8
            UserId = entry.UserId,       // "user-123"
            TenantId = entry.TenantId,   // "tenant-001"
            TraceId = entry.TraceId,     // "00-abc..."
        }, ct);
    }
}

// 注册
builder.Services.AddNexMeshAuditStore<DbAuditStore>();
```

---

## 第十一步:幂等防重

防止用户双击提交按钮导致重复创建:

```csharp
// 启用(中间件)
app.UseNexMeshIdempotency();

// 前端调用时带 Idempotency-Key 请求头:
// POST /api/orders
// Idempotency-Key: unique-key-12345
// {"customerName":"张三","amount":99}
//
// 同一个 Key 的并发请求,只有第一个会被处理,其余返回 409:
// {"success":false,"code":40901,"message":"重复提交,请等待首次请求完成"}
```

---

## 第十二步:限流

```csharp
// 注册(默认每个 IP 60 秒最多 100 次)
builder.Services.AddNexMeshRateLimiter(builder.Configuration);

// 启用
app.UseRateLimiter();

// 超限返回 429:
// {"success":false,"code":429,"message":"请求过于频繁,请稍后重试"}

// 自定义配置(appsettings.json):
// { "NexMesh": { "RateLimiting": { "PermitLimit": 1000, "WindowSeconds": 60 } } }
```

---

## 第十三步:乐观并发

防止两个人同时编辑同一条数据,后提交的覆盖先提交的:

```csharp
// 1. 实体实现 IHasConcurrencyStamp
public class Order : IHasConcurrencyStamp
{
    public string ConcurrencyStamp { get; set; } = "";
    // ... 其他字段
}

// 2. 用户 A 和 B 同时读到同一条数据(戳相同:"abc123")
// 3. A 先提交 → 成功(戳变为 "def456")
// 4. B 后提交 → 409:
//    {"success":false,"code":40902,"message":"数据已被其他操作修改,请刷新后重试"}

// 前端处理:收到 409 时提示用户"数据已被修改,请刷新后重试"
```

---

## 第十四步:数据库迁移

生产环境不要依赖自动建表,用 SQL 脚本:

```
Scripts/
├── V001__create_tables.sql      ← 建表
├── V002__add_indexes.sql        ← 加索引
└── V003__seed_data.sql          ← 种子数据
```

```csharp
// Program.cs:
builder.Services.AddNexMeshMigrations();
var app = builder.Build();
app.ApplyNexMeshMigrations();    // 启动时执行未跑过的脚本,已跑过的自动跳过
```

**种子数据:**

```csharp
// 定义种子(启动时预置基础数据)
public class DictionarySeed(IRepository<Dict> repo) : IDataSeedContributor
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if (await repo.CountAsync() > 0) return;    // 已有数据,跳过
        await repo.AddAsync(new Dict { Code = "status", Name = "状态" }, ct);
    }
}

// 注册
builder.Services.AddNexMeshSeed<DictionarySeed>();

// Program.cs 启动时执行:
app.ApplyNexMeshSeeds();
```

---

## 第十五步:后台任务

```csharp
using NexMesh.Core;

// 继承 NexMeshPeriodicWorker,指定执行周期
public class OrderSyncWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OrderSyncWorker> logger)
    : NexMeshPeriodicWorker(TimeSpan.FromMinutes(5), scopeFactory, logger)
{
    protected override async Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
    {
        // 每轮独立 Scope,可以注入仓储等 Scoped 服务
        var repo = scoped.GetRequiredService<IRepository<Order>>();
        var count = await repo.CountAsync();
        logger.LogInformation("当前 {Count} 条订单", count);
        // 单轮抛异常不会崩溃,记 Error 后等下一轮
    }
}

// 注册
builder.Services.AddNexMeshWorker<OrderSyncWorker>();
// 服务启动后自动开始执行,停机时优雅退出
```

---

## 第十六步:对象映射

```csharp
using NexMesh.Mapping;

// 注册
builder.Services.AddNexMeshMapping();

// 零配置映射(属性名相同的自动映射)
var dto = entity.Adapt<OrderDto>();

// 集中配置(属性名不同或需要转换时)
builder.Services.AddNexMeshMapping(config =>
{
    config.NewConfig<Order, OrderDto>()
        .Map(dest => dest.StatusText, src => src.Status == 1 ? "已完成" : "进行中");
});

// 注入 IMapper 使用
public class OrderService(IMapper mapper)
{
    public OrderDto ToDto(Order order) => mapper.Map<OrderDto>(order);
}
```

---

## 第十七步:HTTP 弹性

调用第三方 API 时自动重试/熔断/超时:

```csharp
using NexMesh.Resilience;

// 注册(所有 HttpClient 自动获得弹性管线)
builder.Services.AddNexMeshResilience();

// 使用(和平时一样注入 IHttpClientFactory)
public class ExternalApiService(IHttpClientFactory factory)
{
    public async Task<string> CallApi()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("https://api.example.com/data");
        // 如果超时/503,自动重试(最多 5 次,指数退避)
        // 如果连续失败,熔断 5 秒
        return await response.Content.ReadAsStringAsync();
    }
}
```

---

## 第十八步:分布式 ID

```csharp
using NexMesh.Toolbox;

// 注册(多实例部署时每个实例用不同 WorkerId 0-63)
builder.Services.AddNexMeshIds(workerId: 1);

// 使用
public class OrderService(IIdGenerator ids)
{
    public Task<Order> Create()
    {
        var order = new Order
        {
            Id = ids.NextUuidV7(),        // UUID v7(时间有序 GUID)
            // 或
            // Id = ids.NextSnowflakeId(),  // 雪花 ID(long,趋势递增)
        };
        return repo.AddAsync(order);
    }
}
```

**怎么选:**
- 数据库内部主键 → 雪花(long,索引友好)
- 对外暴露标识(URL) → UUIDv7(不暴露业务量级)

---

## 第十九步:脱敏

日志或界面上显示手机号/身份证时遮蔽中间部分:

```csharp
using NexMesh.Toolbox;

NexMask.Phone("13812345678");     // "138****5678"
NexMask.IdCard("110101199001011234"); // "110***********1234"
NexMask.Email("abcd@test.com");   // "a***@test.com"
NexMask.Name("张三");              // "张*"

// 非 11 位手机号(如带 +86 前缀)也自动遮蔽:
NexMask.Phone("+8613812345678");  // "+86********678"
```

---

## 第二十步:Excel 导入导出

```csharp
using NexMesh.Toolbox;

// 导出
var bytes = NexExcel.ToBytes(products);    // byte[],写到文件或返回给前端

// 导入
var items = NexExcel.Read<ProductDto>(uploadStream);  // List<ProductDto>
```

---

## 第二十一步:API 版本控制

```csharp
// 启用
app.UseNexMeshApiVersion();

// 客户端带 X-Api-Version 请求头
// X-Api-Version: 2

// 端点内获取版本:
app.MapGet("/api/orders", (HttpContext ctx) =>
{
    var version = ctx.GetApiVersion();     // 2(没传头默认 1)
    return version switch
    {
        1 => "v1 response",
        2 => "v2 response",
        _ => "unknown",
    };
});
```

推荐用 URL 段做版本路由:`/api/v1/orders`、`/api/v2/orders`。

---

## 第二十二步:运行时诊断

```csharp
// 启动时自动输出装配摘要(哪些包生效/什么模式/什么开关)
// 访问 /diagnostics 实时查看
app.UseNexMeshDiagnostics();
```

启动日志:

```
[NexMesh] 装配摘要:5 项能力生效
[NexMesh]   Auth.JWT: Issuer:myapp Expiry:120min
[NexMesh]   Cache.FreeRedis: 缓存源:main(Memory)
[NexMesh]   Core: 健康检查+统一响应+异常+ICurrentUser/ICurrentTenant
[NexMesh]   Data.FreeSql: 数据源:main(Sqlite) 软删除:True 审计:True 租户:False
[NexMesh]   Events.Memory: 进程内直派
```

`/diagnostics` 端点返回:

```json
{
  "framework": "NexMesh",
  "version": "1.1.1",
  "environment": "Development",
  "uptime": "00:15:32",
  "features": { ... },
  "requestMetrics": {
    "GET /api/orders": { "count": 1234, "errors": 5, "p95ms": 45.2 }
  }
}
```

生产环境默认关闭(`/diagnostics` 返回 404)。开启:

```json
{ "NexMesh": { "Diagnostics": { "EnableInProduction": true } } }
```

---

## 第二十三步:日志

默认用 Serilog 输出 JSON 到控制台:

```json
{"@t":"2026-10-20T12:00:00Z","@m":"审计 GET /api/orders → 200","@l":"Information"}
```

Docker/K8s 里看日志:`kubectl logs <pod>`

同时写文件:

```csharp
builder.Host.UseSerilog((ctx, cfg) => cfg
    .WriteTo.Console()
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));
```

---

## 第二十四步:链路追踪

每个请求有唯一 TraceId,出现在三个地方:

1. **响应头 `X-Trace-Id`** → 用户报障时让他复制给你
2. **JSON 响应体 `traceId` 字段** → 错误响应自动带
3. **日志** → 用 TraceId 搜日志找到这个请求的全部记录

```
用户: "接口报错了"
你:   "把响应头 X-Trace-Id 给我"
用户: "00-720c04ea-cc8ba03f-00"
你:   grep "720c04ea" app.log
```

配 OpenTelemetry(可选):

```json
{ "NexMesh": { "Observability": {
    "EnableOpenTelemetry": true,
    "OtlpEndpoint": "http://otel-collector:4317"
} } }
```

---

## 第二十五步:API 文档

```csharp
builder.Services.AddNexMeshDocs();    // 启用 OpenAPI
// ...
app.MapNexMeshDocs();                 // /scalar 调试 UI + /swagger/v1/swagger.json
```

打开 `/scalar` 可以在线调试所有 API。

生产环境默认关闭。开启:

```json
{ "NexMesh": { "Docs": { "EnableInProduction": true } } }
```

---

## 部署

六种部署方式,按场景选。详细步骤见 [部署指南](docs/部署指南.md)。

### 方式一:本地开发

```bash
dotnet run
# 不需要装 Redis/MQ/数据库(框架自动降级到内存/SQLite)
```

### 方式二:Windows 服务

```powershell
dotnet publish -c Release -o C:\Apps\MyApp

# 注册为 Windows 服务(开机自启+崩溃自动重启)
sc.exe create "MyApp" binPath= "C:\Apps\MyApp\MyApp.exe" start= auto
sc.exe failure "MyApp" reset= 86400 actions= restart/5000/restart/10000/restart/60000
sc.exe start "MyApp"
```

### 方式三:IIS

```powershell
dotnet publish -c Release -o C:\publish\MyApp

# IIS 管理器创建网站:
# 1. 添加网站 → 物理路径 C:\publish\MyApp → 端口 8080
# 2. 应用程序池 → .NET CLR 版本改为"无托管代码"(重要!)
```

需要安装 [.NET Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0)。

### 方式四:Linux systemd

```bash
dotnet publish -c Release -o /opt/myapp

sudo tee /etc/systemd/system/myapp.service << 'EOF'
[Unit]
Description=MyApp
After=network.target
[Service]
ExecStart=/usr/bin/dotnet /opt/myapp/MyApp.dll
Restart=always
Environment=ASPNETCORE_URLS=http://*:8080
Environment=NEXMESH_JWT_KEY=生产密钥...
[Install]
WantedBy=multi-user.target
EOF

sudo systemctl enable --now myapp
```

### 方式五:Docker

```bash
docker build -t myapp:1.0.0 .
docker run -d -p 8080:8080 \
  -e NEXMESH_DB="Server=...;Database=..." \
  -e NEXMESH_JWT_KEY="生产密钥至少32字符" \
  --restart unless-stopped \
  myapp:1.0.0
```

### 方式六:Kubernetes

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: myapp
spec:
  replicas: 3
  template:
    spec:
      containers:
        - name: myapp
          image: myregistry/myapp:1.0.0
          ports: [ { containerPort: 8080 } ]
          env:
            - name: NEXMESH_JWT_KEY
              valueFrom: { secretKeyRef: { name: secrets, key: jwt } }
          livenessProbe:
            httpGet: { path: /health/live, port: 8080 }    # 数据库挂了不影响
          readinessProbe:
            httpGet: { path: /health/ready, port: 8080 }   # 数据库挂了摘流量
```

### 怎么选

| 场景 | 推荐方式 |
|---|---|
| 本地开发 | dotnet run |
| 单台 Windows 服务器 | Windows 服务 |
| 已有 IIS | IIS |
| 单台 Linux | systemd |
| 容器化环境 | Docker |
| 大规模微服务 | K8s |
| 客户内网(免装运行时) | 单文件发布 + Windows 服务 |

---

## 运维端点

| 端点 | 用途 |
|---|---|
| `/health` | 综合健康 |
| `/health/live` | K8s 存活探针(依赖挂了不影响) |
| `/health/ready` | K8s 就绪探针 |
| `/diagnostics` | 装配清单 + 请求统计(生产默认关) |
| `/scalar` | API 调试 UI(生产默认关) |

---

## 出错了怎么查

1. **看响应头 `X-Trace-Id`** → 拿这个搜日志
2. **看 JSON 里的 `code` 字段** → 对照错误码表
3. **看 `/diagnostics`** → 确认哪些能力生效了

| 码 | 含义 | 怎么处理 |
|---|---|---|
| 40002 | 参数校验失败 | 看 message 字段 |
| 40401 | 资源不存在 | 检查 Id 是否正确 |
| 40901 | 重复提交 | 前端防双击或用幂等键 |
| 40902 | 数据被别人改了 | 前端提示刷新后重试 |
| 429 | 请求太频繁 | 等一会儿再试 |
| 500 | 服务器内部错误 | 看日志(TraceId 搜) |

更多见 [故障排查指南](docs/故障排查.md)。

---

## 超大型项目实战指南

照着做,从零搭建一个具备 24 项企业级能力(认证/多租户/事件/缓存/审计/幂等/并发/迁移/后台任务/可观测/部署)的完整订单微服务。

16 个章节,每章带完整可复制代码:

| 章 | 内容 |
|---|---|
| 一 | 项目规划(目录结构/垂直切片) |
| 二 | 创建项目(装包/NuGet.config) |
| 三 | 基础设施(Program.cs 完整版 11 包接线 + appsettings 开发/生产两套) |
| 四 | 数据层(Order 实体四接口/DTO/领域服务含库存扣减) |
| 五 | API 层(自动 API 完整接口+实现) |
| 六 | 认证授权(登录签发 token/租户中间件/角色控制) |
| 七 | 多租户(数据隔离/宿主模式) |
| 八 | 事件驱动(3 个处理器) |
| 九 | 缓存策略 |
| 十 | 审计日志(落库) |
| 十一 | 后台任务(超时订单自动取消) |
| 十二 | 可观测性(OTel/TraceId 排障) |
| 十三 | 数据库迁移(建表 SQL/种子) |
| 十四 | 部署(Dockerfile/docker-compose/K8s) |
| 十五 | 测试策略(单元+集成) |
| 十六 | 团队协作(新人 5 步/PR 检查清单/代码审查重点) |

完整指南 → [超大型项目实战指南](docs/超大型项目实战指南.md)

---

## 包清单

| 包 | 干什么 |
|---|---|
| ⚡ [NexMesh.Core](src/NexMesh.Core/README.md) | 统一响应、异常、健康检查、TraceId、限流、ICurrentUser、自动 API |
| 🗄️ [NexMesh.Data.FreeSql](src/NexMesh.Data.FreeSql/README.md) | 仓储、软删除、审计、乐观并发、多租户、分页、long主键雪花ID |
| 🔄 [NexMesh.Data.Migrations](src/NexMesh.Data.Migrations/README.md) | SQL 脚本迁移(V001__xxx.sql 按序执行) |
| 💾 [NexMesh.Cache.FreeRedis](src/NexMesh.Cache.FreeRedis/README.md) | 缓存、防击穿、分布式锁、幂等防重、Redis 降级自愈 |
| 🔐 [NexMesh.Auth](src/NexMesh.Auth/README.md) | JWT(401统一格式)、ApiKey(SHA-256+身份映射) |
| 📨 [NexMesh.Events.CAP](src/NexMesh.Events.CAP/README.md) | 事件总线(进程内零基础设施 / RabbitMQ Outbox) |
| 📊 [NexMesh.Observability](src/NexMesh.Observability/README.md) | Serilog 日志、OpenTelemetry 链路追踪 |
| 🛡️ [NexMesh.Resilience](src/NexMesh.Resilience/README.md) | HTTP 弹性(重试/熔断/超时) |
| 🔄 [NexMesh.Mapping.Mapster](src/NexMesh.Mapping.Mapster/README.md) | 对象映射(Adapt 零配置 + 集中配置) |
| 🧰 [NexMesh.Toolbox](src/NexMesh.Toolbox/README.md) | 雪花 ID、UUIDv7、脱敏、Excel 导入导出 |
| 📖 [NexMesh.Web.Scalar](src/NexMesh.Web.Scalar/README.md) | OpenAPI + Scalar 调试 UI(生产默认关) |

---

## 后续计划

### v1.2 — 开发者体验(近期)

| 功能 | 状态 | 说明 |
|---|---|---|
| 🔌 OIDC 对接 | 🔨 开发中 | 对接 Keycloak/Azure AD 单点登录(替代本地 JWT 签发) |
| 🔍 Nacos 服务发现 | 📋 规划中 | 微服务注册/发现/配置中心 |
| 📝 Refit 客户端生成 | 📋 规划中 | 契约接口自动生成类型安全 HTTP 客户端 |
| 🌐 gRPC 支持 | 💭 评估中 | 契约复用(REST + gRPC 双协议) |

### v1.3 — 生产增强(中期)

| 功能 | 状态 | 说明 |
|---|---|---|
| 🔒 乐观并发真原子 CAS | 📋 规划中 | UPDATE WHERE stamp=old(替代 check-then-act) |
| 📈 指标路由模板化 | 📋 规划中 | /diagnostics 指标按键路由模板(非原始 Path) |
| 📤 Excel 公式注入防护 | 📋 规划中 | =/+/-/@ 开头单元格自动转义 |
| 🏗️ ABP 迁移工具 | 💭 评估中 | 从 ABP 项目半自动迁移到 NexMesh |

### v2.0 — 生态(远期)

| 功能 | 状态 | 说明 |
|---|---|---|
| 🎨 Blazor UI 组件 | 💭 评估中 | 后台管理 UI(表格/表单/图表) |
| 🔗 GraphQL 支持 | 💭 评估中 | HotChocolate 集成 |
| 🤖 AI 辅助编码 | 💭 评估中 | CLI 工具(dotnet nexmesh generate crud) |
| ☁️ 云原生模板 | 💭 评估中 | Aspire AppModel + Dapr 集成 |

> 状态说明:🔨 开发中 / 📋 规划中(已排期) / 💭 评估中(收集需求)

## 更多文档

**完整文档索引** → [docs/README.md](docs/README.md)(文档中心,按角色分类)

### 按场景快速找到

| 我想... | 看这个 |
|---|---|
| 从零搭建一个新项目 | [超大型项目实战指南](docs/超大型项目实战指南.md)(16 章可跟做) |
| 查某个方法怎么用 | 本 README(25 步教程)或对应[包 README](docs/README.md#包-api-文档11-个包) |
| 了解团队编码规范 | [开发规范 17 篇](docs/standards/00-规范总览.md)(合并门禁) |
| 配置开发/生产环境 | [配置指南](docs/配置指南.md)(.env / User Secrets / 环境变量) |
| 部署到服务器 | [部署指南](docs/部署指南.md)(Docker / K8s / IIS / Windows 服务) |
| 接口报错了 | [故障排查](docs/故障排查.md)(TraceId / 错误码 / 12 个常见坑) |
| 让 AI 助手理解 NexMesh | [AI 工具对接](docs/AI工具对接指南.md)(Claude / Cursor / Copilot) |
| 了解为什么这样设计 | [设计决策与路线图](docs/设计决策与路线图.md) |
| 查错误码含义 | [错误码登记表](docs/errorcodes.md) |
| 看版本变更 | [CHANGELOG](CHANGELOG.md) |

## 环境

.NET 10 SDK。

## License

MIT
