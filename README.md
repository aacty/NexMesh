<div align="center">

# NexMesh

**你只写业务代码，框架处理其余一切**

.NET 10 企业级 AI 与业务融合框架

129 项测试全绿 · SSS+ 双轮安全审查通过 · 零构建警告

</div>

---

## 框架能做什么

| 你只需要写 | 框架自动处理 |
|---|---|
| POCO 实体 + 接口约定 | 软删/审计/租户隔离/并发/雪花ID 全自动 |
| 业务接口 + `[Http*]` 标注 | REST 路由/参数绑定/授权/文档 全自动 |
| `[NexMeshTool(描述, 风险)]` 方法 | AI 工具注册/安全门/遥测/语义召回 |
| 继承 `NexMeshWorkflowStep` | 检查点持久化/HITL挂起恢复/CAS防并发 |

## 30 秒上手

```bash
dotnet new install ./templates/NexMesh.Templates/content
dotnet new nexmesh-ai -n MyApp
cd MyApp && dotnet run
# 打开 http://localhost:5180/health
```

或者最小化手写：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddNexMeshService();               // 一行总装
var app = builder.Build();
app.UseNexMeshService();                   // 一行管线
app.Run();
```

## 11 个包

| 包 | 定位 |
|---|---|
| **NexMesh.Host** | 一行总装：配置/约定注入/FreeSql/AI管道/中间件 |
| **NexMesh.Core** | 约定注入 · AutoApi(完整参数绑定) · 虚拟网关(10M+路由) · 上下文显式绑定 · 三层防御 |
| **NexMesh.AI** | Chat管道四层 · Polly弹性 · 断连熔断 · 工具三防 · ToolSearch语义召回 · 实例工具一等公民 |
| **NexMesh.AI.Approval** | 审批门：High工具挂起拦截 · 幂等键 · 快照随队 · 批准后再水化执行 |
| **NexMesh.AI.SemanticCache** | 语义缓存：嵌入向量→余弦命中秒回(成本降50%+) · 租户隔离 |
| **NexMesh.AI.Workflow** | 工作流：步骤序列 · Checkpoint持久化 · HITL挂起恢复 · CAS防并发 |
| **NexMesh.Auth** | 不透明Token · 登录限流 · 滑动续期 · AutoApi `[Authorize]` 标注 · 默认拒绝 |
| **NexMesh.Cache** | 双层缓存(L1+L2) · 防击穿 · 分布式锁 · 幂等防重 |
| **NexMesh.Data.FreeSql** | 实体约定自动接管：软删/审计/租户过滤/并发/雪花 · 多库 · 仓储 |
| **NexMesh.Toolbox** | Excel导入导出 · 文件Blob存储(Db/Local) · SMTP邮件 · 数据脱敏 |
| **NexMesh.Worker** | Cron/Interval定时调度 · 异常隔离 · 系统身份快照 |

## 安全体系

| 防线 | 机制 |
|---|---|
| **默认拒绝三件套** | AutoApi(未标注→requireAuth) / 网关(handler 401) / 审批中心(默认 requireAuthorization=true) |
| **租户隔离三层闭环** | 全局过滤(ITenantEntity) / 写入自动填充(FillCreate) / 缓存键隔离(scope 前缀) |
| **AI 安全门** | High 风险工具发现即阻断(未启用审批门) / 启用后挂起拦截(终局语义+幂等键) |
| **三层防御** | 断连熔断(浏览器关=计费停) / 上下文穿帮(AsyncLocal 快照显式绑定) / 工具自纠错(参数错不崩溃) |
| **登录限流** | per-IP 固定窗口(默认5次/60秒)→429(42901) |
| **信息泄露防护** | 500 不暴露 ex.Message / 工具异常只回喂类型名 / 生产不暴露 API 文档 |

## 完整业务示例

```csharp
// ── 实体：实现接口即自动接管 ──
public class Employee : ISoftDelete, IAudited, IUserAudited
{
    [Column(IsIdentity = true)] public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public bool IsDeleted { get; set; }         // 软删自动
    public DateTime CreateTime { get; set; }    // 审计自动
    public string? CreateUserId { get; set; }   // 操作人自动
}

// ── 接口：声明即 REST API ──
public interface IEmployeeBiz : IAutoApi
{
    Task<long> CreateAsync(EmployeeDto dto);                       // POST   /api/employee/createasync
    [HttpGet("{id}")] Task<EmployeeDto?> GetAsync(long id);        // GET    /api/employee/{id}
    [HttpPut("{id}")] Task<bool> UpdateAsync(long id, EmployeeDto d); // PUT /api/employee/{id}
    [HttpDelete("{id}")] Task<bool> DeleteAsync(long id);          // DELETE /api/employee/{id}
    Task<PagedData<EmployeeDto>> SearchAsync(string? key, int pageIndex = 1, int pageSize = 20);
}

// ── 实现：安安静静写业务 ──
[ServiceRoute("api/employee")]
public class EmployeeService(IFreeSql db, IRepository<Employee> repo)
    : IEmployeeBiz, IScopedService
{
    public Task<EmployeeDto?> GetAsync(long id) { /* 直接查 */ }
    public Task<bool> DeleteAsync(long id) => repo.SoftDeleteAsync(id);
}

// ── AI 工具：同一个服务类 ──
public class OrderTools(IFreeSql db) : IScopedService
{
    [NexMeshTool("查询订单状态。低风险只读。", NexMeshRiskLevel.Low)]
    public Task<string> QueryAsync(long orderId) { /* 查库 */ }

    [NexMeshTool("强制退款。高危写操作!", NexMeshRiskLevel.High)]
    public Task<string> RefundAsync(long orderId, string reason) { /* 挂起等审批 */ }
}
```

## 文档

| 文档 | 内容 |
|---|---|
| [快速开始](docs/快速开始.md) | 5 分钟跑通第一个服务 |
| [配置参考](docs/配置参考.md) | 全部配置节 · 环境变量 · 占位符 |
| [错误码参考](docs/错误码参考.md) | 40xxx/50xxx 完整清单 |
| [安全规范](docs/安全规范.md) | 安全架构 · 审批门 · 租户隔离 · 三层防御 |
| [架构白皮书](docs/架构白皮书.md) | 四层架构 · 设计决策 · 包依赖 |
| [历史文档](docs/history/) | 旧版白皮书 · 审查记录 · v1 遗留文档 |

## 技术栈

.NET 10 · Microsoft.Extensions.AI 10.11 · FreeSql 3.5 · StackExchange.Redis 3.4 · Polly 8.8 · MiniExcel · Cronos

## License

[MIT](LICENSE)
