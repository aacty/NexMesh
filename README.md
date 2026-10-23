<div align="center">

# NexMesh

**你只写业务逻辑，框架处理其余一切**

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com)
[![Tests](https://img.shields.io/badge/tests-178%20passed-brightgreen?style=flat-square)]()
[![Security](https://img.shields.io/badge/security-SSS%2B%20reviewed-blue?style=flat-square)]()
[![License](https://img.shields.io/badge/license-MIT-orange?style=flat-square)](LICENSE)
[![Packages](https://img.shields.io/badge/NuGet-0.0.4-purple?style=flat-square)](https://www.nuget.org/packages?q=NexMesh)

**企业级 AI 与业务融合框架** — 让 CRUD 和 AI 工具调用共享同一套编程模型

[快速开始](#快速开始) · [为什么选择 NexMesh](#为什么选择-nexmesh) · [30 秒体验](#30-秒体验) · [文档](#文档)

</div>

---

## 为什么选择 NexMesh

### 没有 NexMesh 的世界

构建一个包含 AI 能力的企业级后端，你需要：

| 手动处理 | 涉及的包/代码量 |
|---|---|
| DI 注册 + 生命周期管理 | 每个服务手动 `AddScoped<T>()` |
| 路由映射 + 参数绑定 | Controller/Endpoint 手写，每个参数单独处理 |
| 认证 + 授权 + Token 管理 | JWT/ApiKey 配置、过期、续期、吊销 |
| 软删除 + 审计字段 + 多租户 | 每个查询手动加 `WHERE`，每个写入手动填字段 |
| AI 工具注册 + 安全管控 | 手写 `AIFunction` 包装、参数解析、风险拦截 |
| 缓存 + 防击穿 + 分布式锁 | 选型 + 接入 + 防御性编程 |
| 断连后模型继续烧钱 | 没人处理，直到账单到来 |
| 跨线程身份丢失 | 后台任务读不到当前用户，偶现且难排查 |
| 大模型 JSON 参数写错 | 直接崩溃，Agent 循环中断 |

### 有 NexMesh 的世界

```csharp
// 这就是一个完整的企业级 AI 服务（认证+CRUD+AI工具+审批门）
// 没有DI注册、没有路由配置、没有中间件顺序、没有AI管道搭建

public interface IOrderApi : IServiceApi
{
    [HttpDelete("{id}")]
    Task<bool> DeleteAsync(long id);
    // ... 其余方法同理标注 [Http*]，路由/绑定/授权/文档全自动
}

public class OrderService(IFreeSql db, IRepository<Order> repo)
    : IOrderApi, IScopedService
{
    // 普通 CRUD —— 软删/审计/操作人全部自动
    public Task<bool> DeleteAsync(long id) => repo.SoftDeleteAsync(id);

    // AI 工具 —— 自动注册为大模型可调用的 Function
    [NexMeshTool("查询订单状态。低风险只读。", NexMeshRiskLevel.Low)]
    public async Task<string> QueryAsync(long orderId)
        => (await db.Select<Order>().Where(o => o.Id == orderId).FirstAsync())?.ToString()
           ?? $"订单 {orderId} 不存在";

    // 高危 AI 工具 —— 自动挂起等审批，批准后按触发者身份执行
    [NexMeshTool("强制退款。高危写操作！", NexMeshRiskLevel.High)]
    public async Task<string> RefundAsync(long orderId, string reason)
        => $"订单 {orderId} 已退款：{reason}";
}
```

**一个类 = REST API + AI 工具 + 审批门 + 审计追踪。**

---

## 30 秒体验

```bash
# 克隆并运行（零配置，内置 SQLite + Stub LLM）
git clone https://github.com/aacty/NexMesh.git
cd NexMesh/samples/NexMesh.AiDemo
dotnet run
```

```bash
# 健康检查
curl http://localhost:5099/health
# → Healthy

# 登录
TOKEN=$(curl -s -X POST http://localhost:5099/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"doctor","password":"123456"}' | jq -r '.data.accessToken')

# 员工分页检索（3000 条数据）
curl -X POST "http://localhost:5099/api/employee/Search?pageIndex=2&pageSize=5" \
  -H "Authorization: Bearer $TOKEN"
# → {"success":true,"code":0,"data":{"totalCount":3000,"items":[...]}}

# 流式 AI 对话
curl -N -X POST http://localhost:5099/api/ai/chat \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"prompt":"hello"}'
# → data: [NexMesh Stub] 这是一段离线流式演示输出。
```

---

## 快速开始

<details>
<summary><b>方式一：脚手架（推荐）</b></summary>

```bash
dotnet new install ./templates/NexMesh.Templates/content
dotnet new nexmesh-ai -n MyApp
cd MyApp && dotnet run
```

</details>

<details>
<summary><b>方式二：从零搭建</b></summary>

```bash
dotnet new web -n MyApp && cd MyApp
dotnet add package NexMesh.Host          # 或本地源码:dotnet add reference ../NexMesh/src/NexMesh.Host/NexMesh.Host.csproj
```

创建 `Configs/app.json`：

```json
{
  "NexMesh": {
    "Db": { "Type": "Sqlite", "ConnectionString": "Data Source=app.db", "AutoSyncStructure": true },
    "AI": { "Providers": { "main": { "Type": "stub", "Model": "stub" } } }
  }
}
```

`Program.cs`：

```csharp
using NexMesh.Host;

var builder = WebApplication.CreateBuilder(args);
builder.AddNexMeshService();     // ← 这一行装了：配置/约定注入/FreeSql/AI管道/缓存/中间件

var app = builder.Build();
app.UseNexMeshService();         // ← 这一行装了：上下文快照/异常/CORS/契约端点/健康/文档
app.Run();
```

</details>

<details>
<summary><b>接入真实 LLM（DeepSeek / OpenAI / Ollama）</b></summary>

```json
{
  "NexMesh": {
    "AI": {
      "DefaultProvider": "deepseek",
      "Providers": {
        "deepseek": {
          "Type": "openai-compatible",
          "Endpoint": "https://api.deepseek.com/v1",
          "ApiKey": "${NEXMESH_DEEPSEEK_KEY|}",
          "Model": "deepseek-chat"
        },
        "ollama": {
          "Type": "openai-compatible",
          "Endpoint": "http://localhost:11434/v1",
          "Model": "qwen3:8b"
        }
      }
    }
  }
}
```

`${ENV|默认值}` 占位符自动从环境变量读取 Key——不落盘、不泄露。

</details>

---

## 核心能力

### 业务层 —— 写更少的代码

| 能力 | 你写的 | 框架做的 |
|---|---|---|
| REST API | `interface IOrderApi : IServiceApi` + `[HttpGet("{id}")]` | 路由/绑定/授权/文档/参数校验 |
| 数据实体 | `class Order : ISoftDelete, IAudited` | 软删过滤/审计填充/并发戳/雪花ID |
| 多租户 | `class Order : ITenantEntity` | 全局过滤/写入填充/缓存隔离 |
| 授权 | `[Authorize]` 标注 | 默认拒绝/401/403/RequireAuthorization |

### AI 层 —— 零样板接入大模型

| 能力 | 你写的 | 框架做的 |
|---|---|---|
| AI 工具 | `[NexMeshTool("描述", Risk)]` 方法 | 注册/元数据/遥测/安全门/自纠错 |
| 高危管控 | `Risk = NexMeshRiskLevel.High` | 挂起→审批队列→批准→按触发者身份执行 |
| 语义缓存 | `"SemanticCache": { "Enabled": true }` | 嵌入向量→余弦命中→秒回缓存(成本降50%+) |
| 工具召回 | `"ToolSearch": { "Enabled": true }` | 用户意图→语义排序→Top-K 进 Prompt |
| 弹性 | `"Resilience": { }` | 重试→熔断→超时(Polly) |
| 断连保护 | 无需写 | 浏览器关闭=模型流/计费立即终止 |

### 平台层 —— 企业级基础设施

| 能力 | 一行调用 |
|---|---|
| 认证 | `AddNexMeshAuth()` 装配 + `MapNexMeshAuth()` 挂端点（登录/登出/me）+ 登录限流 + 滑动续期 |
| 缓存 | `AddNexMeshCache()` → L1+L2 + 防击穿 + 分布式锁 + 幂等 |
| 定时任务 | `AddNexMeshCronJob<T>("0 2 * * *")` → Cron 调度 + 异常隔离 + 系统身份 |
| 文件存储 | `AddNexMeshFileStorage()` → DB Blob / 本地磁盘 / OSS 同接口 |
| Excel | `NexMeshExcel.ExportAsync(stream, rows)` |
| 邮件 | `AddNexMeshEmail()` → SMTP |
| 工作流 | `AddNexMeshWorkflow()` → Checkpoint 持久化 + HITL 挂起恢复 |

---

## 安全架构

NexMesh 从第一行代码就按医疗级安全标准设计，经过 **SSS+ 双轮独立安全审查**。

```
┌────────────────────────────────────────────────────────┐
│                    默认拒绝                              │
│  契约端点未标注 → 401  ·  网关 → 401  ·  审批中心 → 401 │
├────────────────────────────────────────────────────────┤
│                  租户隔离（三层闭环）                     │
│  读：全局过滤    写：自动填充    缓存：键隔离            │
├────────────────────────────────────────────────────────┤
│                   AI 安全门                              │
│  High 工具 → 挂起 → 审批队列 → 批准 → 按触发者执行      │
│  幂等键防重复 · 审批人不可伪造 · 终局语义禁止编造        │
├────────────────────────────────────────────────────────┤
│                  三层防御                                │
│  断连熔断(计费停) · 上下文穿帮防护 · 参数自纠错          │
├────────────────────────────────────────────────────────┤
│                  信息泄露防护                             │
│  500不暴露ex.Message · 工具异常只回类型名 · 生产关文档  │
│  CORS默认拒绝 · 登录限流5次/分钟 · 会话惰性清扫          │
└────────────────────────────────────────────────────────┘
```

---

## 包架构

```
                ┌──────────────┐
                │ NexMesh.Host │  ← 业务项目只引这个
                └──────┬───────┘
           ┌───────────┼───────────┐
           ▼           ▼           ▼
    ┌────────────┐ ┌────────┐ ┌─────────┐
    │NexMesh.Core│ │NexMesh │ │NexMesh  │
    │  (底座)    │ │  .AI   │ │Data.FreeSql│
    └─────┬──────┘ └───┬────┘ └─────────┘
          │              │
          │        ┌─────┼──────────┐
          │        ▼     ▼          ▼
          │  ┌─────────┐┌──────────┐┌──────────┐
          │  │Approval ││Semantic  ││Workflow  │
          │  │ (审批门)││  Cache   ││ (工作流) │
          │  └─────────┘└──────────┘└──────────┘
          │
    ┌─────┼──────┬──────────┬─────────┐
    ▼     ▼      ▼          ▼         ▼
┌──────┐┌─────┐┌────────┐┌────────┐┌───────┐
│Auth  ││Cache││Toolbox ││Worker  ││Cache  │
│(认证)││(缓存)││(工具箱)││(调度)  ││       │
└──────┘└─────┘└────────┘└────────┘└───────┘
```

| 包 | NuGet 依赖 |
|---|---|
| Core | —（纯 BCL + ASP.NET Core） |
| AI | M.E.AI 10.11 · OpenAI 2.14 · Polly 8.8 |
| Auth | —（纯 ASP.NET Core） |
| Cache | StackExchange.Redis 3.4 |
| Data.FreeSql | FreeSql 3.5 |
| Toolbox | MiniExcel 1.46 |
| Worker | Cronos 0.13 |

---

## 项目状态

**已发布 [NuGet.org](https://www.nuget.org/packages?q=NexMesh)（0.0.4，11 包）**：`dotnet add package NexMesh.Host` 即用。

### ✅ 已交付（19 项核心能力全部可用）

<details>
<summary>点击展开完整清单</summary>

| # | 能力 | 包 |
|---|---|---|
| 1 | 一行总装 | Host |
| 2 | 约定注入 | Core |
| 3 | 契约端点引擎（IServiceApi 完整参数绑定） | Core |
| 4 | 虚拟网关（10M+ 路由） | Core |
| 5 | 上下文显式绑定 | Core |
| 6 | 三层防御 | Core + AI |
| 7 | 认证 + 登录限流 | Auth |
| 8 | 授权 + 默认拒绝 | Core + Auth |
| 9 | AI Chat 管道 | AI |
| 10 | AI 工具（静态+实例） | AI |
| 11 | 审批门 | AI.Approval |
| 12 | 语义缓存 | AI.SemanticCache |
| 13 | ToolSearch 语义召回 | AI |
| 14 | 工作流 + HITL | AI.Workflow |
| 15 | 数据层约定 | Data.FreeSql |
| 16 | 双层缓存 | Cache |
| 17 | 定时调度 | Worker |
| 18 | 工具箱 | Toolbox |
| 19 | 脚手架模板 | templates |

</details>

### 🔄 进行中

| 项 | 说明 |
|---|---|
| CI/CD | GitHub Actions 适配新结构 |

### 📋 路线图

| 项 | 优先级 |
|---|---|---|
| OIDC 单点登录 | 中 |
| gRPC 内部通信 | 中 |
| 数据库 SQL 迁移 | 中 |
| 事件总线（CAP） | 低 |
| 性能基准 | 低 |
| Blazor 管理界面 | 低 |

---

## 文档

| 文档 | 你会学到 |
|---|---|
| [快速开始](docs/快速开始.md) | 5 分钟跑通第一个服务 |
| [配置参考](docs/配置参考.md) | 11 个配置节全覆盖 |
| [错误码参考](docs/错误码参考.md) | 异常族 + 40xxx/50xxx 完整清单 + 前端处理指南 |
| [安全规范](docs/安全规范.md) | 安全架构 + 审批门 + 租户隔离 |
| [架构白皮书](docs/架构白皮书.md) | 四层架构 + 包依赖 + 路线图 |

---

## 技术栈

| 类别 | 技术 |
|---|---|
| 运行时 | .NET 10 |
| Web | ASP.NET Core Minimal API |
| AI | Microsoft.Extensions.AI 10.11 |
| ORM | FreeSql 3.5 |
| 缓存 | StackExchange.Redis 3.4 |
| 弹性 | Polly 8.8 |
| 调度 | Cronos 0.13 |
| Excel | MiniExcel 1.46 |
| 测试 | xUnit 2.9（178 项） |

---

## License

[MIT](LICENSE)
