<div align="center">

# NexMesh

**你只写业务代码，框架处理其余一切**

.NET 10 企业级 AI 与业务融合框架

</div>

---

## 项目状态

**当前版本：v2.0.0-alpha.1 · 129 项测试全绿 · 零构建警告 · SSS+ 双轮安全审查通过**

### 已完成 ✅

| 能力域 | 状态 | 包 |
|---|---|---|
| 一行总装（配置/DI/数据库/AI/中间件） | ✅ 可用 | Host |
| 约定注入（标记接口自动注册） | ✅ 可用 | Core |
| AutoApi（接口声明→REST，完整参数绑定） | ✅ 可用 | Core |
| 虚拟网关（10M+ 逻辑路由） | ✅ 可用 | Core |
| 上下文显式绑定（跨线程/离线断链） | ✅ 可用 | Core |
| 认证（不透明 Token/登录限流/滑动续期） | ✅ 可用 | Auth |
| 授权（[Authorize] 标注/默认拒绝） | ✅ 可用 | Core + Auth |
| AI Chat 管道（弹性/断连熔断/遥测/函数调用） | ✅ 可用 | AI |
| AI 工具注册（静态+实例/安全门/自纠错） | ✅ 可用 | AI |
| 审批门（High 工具挂起→批准→执行） | ✅ 可用 | AI.Approval |
| 语义缓存（向量命中秒回/租户隔离） | ✅ 可用 | AI.SemanticCache |
| ToolSearch（语义召回 Top-K） | ✅ 可用 | AI |
| 工作流（步骤/CHECKPOINT/HITL 恢复） | ✅ 可用 | AI.Workflow |
| 数据层（软删/审计/租户/并发/雪花/仓储） | ✅ 可用 | Data.FreeSql |
| 双层缓存（防击穿/分布式锁/幂等防重） | ✅ 可用 | Cache |
| 定时调度（Cron/Interval/异常隔离） | ✅ 可用 | Worker |
| 工具箱（Excel/文件 Blob/邮件/脱敏） | ✅ 可用 | Toolbox |
| 脚手架模板 | ✅ 可用 | templates |
| 安全体系（默认拒绝/租户三层/三层防御） | ✅ 审查通过 | — |

### 进行中 🔄

| 项 | 说明 |
|---|---|
| NuGet 包发布 | 模板需要 NuGet 包（当前仅项目引用可用） |
| CI/CD 流水线 | GitHub Actions 配置待适配新结构 |

### 计划中 📋

| 项 | 优先级 | 说明 |
|---|---|---|
| OIDC 单点登录 | 中 | 对接 IdentityServer/Keycloak/Auth0 |
| gRPC 支持 | 中 | 高性能内部通信 |
| 服务发现（Nacos/Consul） | 低 | 微服务注册与发现 |
| 事件总线（CAP） | 低 | 进程内/RabbitMQ 事件 |
| 数据库迁移（SQL 脚本） | 中 | 版本化 Schema 管理 |
| 性能基准（BenchmarkDotNet） | 低 | 管道开销/内存分配量化 |
| Blazor 管理界面 | 低 | 内置审批中心/用户管理 UI |
| GraphQL 端点 | 低 | 灵活查询 |

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

## 11 个包

| 包 | 定位 |
|---|---|
| **NexMesh.Host** | 一行总装：配置/约定注入/FreeSql/AI管道/中间件 |
| **NexMesh.Core** | 约定注入 · AutoApi · 虚拟网关 · 上下文显式绑定 · 三层防御 |
| **NexMesh.AI** | Chat管道 · 弹性 · 断连熔断 · 工具三防 · ToolSearch · 实例工具 |
| **NexMesh.AI.Approval** | 审批门：High 工具挂起拦截 · 幂等键 · 批准后再水化执行 |
| **NexMesh.AI.SemanticCache** | 语义缓存：向量命中秒回 · 租户隔离 |
| **NexMesh.AI.Workflow** | 工作流：Checkpoint · HITL 挂起恢复 · CAS 防并发 |
| **NexMesh.Auth** | 不透明 Token · 登录限流 · 滑动续期 · 默认拒绝 |
| **NexMesh.Cache** | 双层缓存(L1+L2) · 防击穿 · 分布式锁 · 幂等防重 |
| **NexMesh.Data.FreeSql** | 实体约定自动接管 · 多库 · 仓储 |
| **NexMesh.Toolbox** | Excel · 文件Blob · 邮件 · 脱敏 |
| **NexMesh.Worker** | Cron/Interval 调度 · 异常隔离 · 系统身份 |

## 安全体系

| 防线 | 机制 |
|---|---|
| **默认拒绝** | 未标注端点→401；审批中心→requireAuth；网关→handler 401 |
| **租户隔离** | 全局过滤 + 写入填充 + 缓存键隔离（三层闭环） |
| **AI 安全门** | High 工具启动阻断(未启用审批门) / 挂起拦截(启用后) |
| **三层防御** | 断连熔断 · 上下文穿帮防护 · 工具参数自纠错 |
| **信息泄露** | 500 不暴露 ex.Message · 工具异常只回喂类型名 · 生产不暴露文档 |

## 完整业务示例

```csharp
// ── 实体：实现接口即自动接管 ──
public class Employee : ISoftDelete, IAudited, IUserAudited
{
    [Column(IsIdentity = true)] public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }         // 软删自动
    public DateTime CreateTime { get; set; }    // 审计自动
}

// ── 接口：声明即 REST API ──
public interface IEmployeeBiz : IAutoApi
{
    Task<long> CreateAsync(EmployeeDto dto);                       // POST /api/employee/createasync
    [HttpGet("{id}")] Task<EmployeeDto?> GetAsync(long id);        // GET  /api/employee/{id}
    [HttpDelete("{id}")] Task<bool> DeleteAsync(long id);          // DELETE(软删)
    Task<PagedData<EmployeeDto>> SearchAsync(string? key, int pageIndex = 1, int pageSize = 20);
}

// ── 实现：安安静静写业务 ──
[ServiceRoute("api/employee")]
public class EmployeeService(IFreeSql db, IRepository<Employee> repo)
    : IEmployeeBiz, IScopedService
{
    public Task<bool> DeleteAsync(long id) => repo.SoftDeleteAsync(id);
}

// ── AI 工具：同一个服务类 ──
public class OrderTools(IFreeSql db) : IScopedService
{
    [NexMeshTool("查询订单状态。低风险只读。", NexMeshRiskLevel.Low)]
    public async Task<string> QueryAsync(long orderId) { /* 查库 */ }

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
| [安全规范](docs/安全规范.md) | 安全架构 · 审批门 · 租户隔离 |
| [架构白皮书](docs/架构白皮书.md) | 四层架构 · 设计决策 · 包依赖 |

## 技术栈

.NET 10 · Microsoft.Extensions.AI 10.11 · FreeSql 3.5 · StackExchange.Redis 3.4 · Polly 8.8 · Cronos · MiniExcel

## License

[MIT](LICENSE)
