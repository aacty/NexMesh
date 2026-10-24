<div align="center">

# NexMesh

**你只写业务逻辑,框架处理其余一切**

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com)
[![Tests](https://img.shields.io/badge/tests-211%20passed-brightgreen?style=flat-square)]()
[![License](https://img.shields.io/badge/license-MIT-orange?style=flat-square)](LICENSE)
[![Packages](https://img.shields.io/badge/NuGet-0.0.4-purple?style=flat-square)](https://www.nuget.org/packages?q=NexMesh)

用 .NET 写后端:接口、登录、数据库、缓存、定时任务、AI 对接——
传统要配十几样东西,NexMesh 里**大部分只写一行或一个标注**。

**第一次接触?** [快速开始](docs/快速开始.md)(15 分钟从零跑通)→ [包实战案例](docs/包实战案例.md)(12 个包逐个上手)→ [大型项目指南](docs/大型项目指南.md)(项目变大后怎么组织)

</div>

---

## NexMesh 是什么

三句话说清:

1. 一个 **.NET 10 后端框架**:写一个接口(定义 + 实现两个类),路由、参数绑定、权限、接口文档自动生成;
2. **AI 和业务共享同一套代码**:业务方法加一个 `[NexMeshTool]` 标注,就变成大模型可以调用的工具;高危操作自动转人工审批;
3. **企业设施开箱即用**:登录令牌、软删除、审计字段、多租户、缓存、定时任务、事件总线——不用再挨个选型和拼装。

## 30 秒体验(克隆即跑,零配置)

```bash
git clone https://gitee.com/520vip/NexMesh.git
cd NexMesh/samples/NexMesh.AiDemo
dotnet run
```

> 内置 SQLite 数据库(3000 条演示数据)和离线 AI 模式,**不需要**装数据库、不需要 AI Key。
> 启动后看控制台 `Now listening on: http://localhost:xxxx` 一行,下面命令里的 `<端口>` 换成它。

另开一个终端:

```bash
# ① 健康检查
curl http://localhost:<端口>/health
# → Healthy

# ② 登录拿令牌(演示账号)
TOKEN=$(curl -s -X POST http://localhost:<端口>/nexmesh/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"doctor","password":"123456"}' | jq -r '.data.accessToken')

# ③ 查业务数据(分页,3000 条演示数据)
curl -X POST "http://localhost:<端口>/api/employee/search?pageIndex=1&pageSize=5" \
  -H "Authorization: Bearer $TOKEN"
# → {"success":true,"code":0,"data":{"totalCount":3000,"items":[...]}}

# ④ AI 对话(离线演示模式,流式返回)
curl -N -X POST http://localhost:<端口>/api/ai/chat \
  -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  -d '{"prompt":"你好"}'
```

---

## 快速开始

<details>
<summary><b>方式一:脚手架(推荐)</b></summary>

```bash
dotnet new install ./templates/NexMesh.Templates/content
dotnet new nexmesh-ai -n MyApp
cd MyApp && dotnet run
```

</details>

<details>
<summary><b>方式二:从空项目手动搭(想看清每一块)</b></summary>

```bash
dotnet new web -n MyApp && cd MyApp
dotnet add package NexMesh.Host --version 0.0.4
```

创建 `appsettings.json`:

```json
{
  "NexMesh": {
    "Db": { "Type": "Sqlite", "ConnectionString": "Data Source=app.db", "AutoSyncStructure": true },
    "AI": { "Providers": { "main": { "Type": "stub", "Model": "stub" } } }
  }
}
```

`Program.cs` 全部内容:

```csharp
using NexMesh.Host;

var builder = WebApplication.CreateBuilder(args);
builder.AddNexMeshService();   // 装配:配置/DI约定/数据库/AI管道/缓存

var app = builder.Build();
app.UseNexMeshService();       // 管线:上下文/异常/CORS/契约端点/健康检查/文档
app.Run();
```

</details>

<details>
<summary><b>接入真实大模型(DeepSeek / OpenAI / Ollama)</b></summary>

```json
{
  "NexMesh": {
    "AI": {
      "DefaultProvider": "deepseek",
      "Providers": {
        "deepseek": {
          "Type": "openai-compatible",
          "Endpoint": "https://api.deepseek.com",
          "ApiKey": "${NEXMESH_DEEPSEEK_KEY|}",
          "Model": "deepseek-flash"
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

`${环境变量名|默认值}` 占位符自动从环境变量读 Key——不落盘、不进 git。

</details>

---

## 它能帮你做什么

### 写接口:两个类搞定一个 API

```csharp
// ① 声明接口(它就是 API 的"合同",路由自动生成)
public interface IOrderApi : IServiceApi
{
    [HttpDelete("{id}")]
    Task<bool> DeleteAsync(long id);
}

// ② 实现它(自动注册,不用写 AddScoped)
public class OrderService(IRepository<Order> repo) : IOrderApi, IScopedService
{
    public Task<bool> DeleteAsync(long id) => repo.SoftDeleteAsync(id);
    // 删除自动是"软删除",创建人/创建时间自动记录——都不用你写
}
```

### 接 AI:一个标注,业务方法变 AI 工具

```csharp
[NexMeshTool("查询订单状态。低风险只读。", NexMeshRiskLevel.Low)]
public async Task<string> QueryAsync(long orderId) { /* 查库返回文本 */ }

[NexMeshTool("强制退款。高危写操作!", NexMeshRiskLevel.High)]
public async Task<string> RefundAsync(long orderId, string reason) { /* ... */ }
// 高危的不会直接执行:AI 触发 → 生成审批单 → 人工批准 → 按触发者身份执行
```

### 平台设施:一行一个

| 能力 | 一行调用 | 替你处理的 |
|---|---|---|
| 认证 | `AddNexMeshAuth()` + `MapNexMeshAuth()` | 登录端点/令牌/续期/吊销/登录限流 |
| 缓存 | `AddNexMeshCache()` | 本地+Redis 双层/防击穿/分布式锁/幂等 |
| 定时任务 | `AddNexMeshCronJob<T>("0 2 * * *")` | Cron 调度/异常隔离/系统身份 |
| 事件总线 | `AddNexMeshEvents()` | 有界队列/重试/停机排干(进程内解耦) |
| 工作流 | `AddNexMeshWorkflow()` | 检查点持久化/人工介入后恢复 |
| 语义缓存 | `AddNexMeshSemanticCache()` | 相似问题秒回,省模型调用 |
| 文件存储 | `AddNexMeshFileStorage()` | DB Blob/磁盘同一接口 |
| Excel | `NexMeshExcel.ExportAsync(stream, rows)` | 流式导入导出 |
| 邮件 | `AddNexMeshEmail()` | SMTP |

---

## 安全设计

按企业级安全标准设计,经过双轮独立安全审查。日常开发记住五条:

1. **接口默认要登录**:没标注的契约端点一律 401;想放开标 `[AllowAnonymous]`;
2. **高危 AI 操作必须人工审批**:`NexMeshRiskLevel.High` 的工具由 AI 触发时挂起,批准后才执行,审批人不可伪造;
3. **多租户三层闭环**:读全局过滤、写自动填充、缓存键隔离——两套部署看不到对方数据;
4. **断连即停**:浏览器关闭,模型流式输出与计费立即终止;
5. **信息不泄露**:500 不暴露异常详情、生产关文档页、CORS 默认拒绝、登录限流 5 次/分钟。

完整机制见[安全规范](docs/安全规范.md)。

---

## 包架构

**业务项目通常只引 `NexMesh.Host`**,其余全部自动传递,按需再引特性包:

```
NexMesh.Host               ← 业务项目只引这一个
├── NexMesh.Core           契约端点引擎 / 上下文 / 异常 / 健康检查 / 接口文档
├── NexMesh.AI             Chat 管道 / [NexMeshTool] 工具注册 / 弹性 / 断连保护
│   ├── NexMesh.AI.Approval       高危工具审批门
│   ├── NexMesh.AI.SemanticCache  语义缓存
│   └── NexMesh.AI.Workflow       工作流(检查点 / 挂起恢复)
├── NexMesh.Auth           登录 / 令牌 / 登录限流
├── NexMesh.Data.FreeSql   实体约定 / 多库 / 软删除 / 审计
├── NexMesh.Events         进程内事件总线
├── NexMesh.Cache          双层缓存 / 幂等
├── NexMesh.Worker         Cron / 间隔作业
└── NexMesh.Toolbox        Excel / 文件存储 / 邮件 / 脱敏
```

| 包 | 干什么 | 外部依赖 |
|---|---|---|
| Core | 契约端点引擎/上下文/异常 | 无(纯 ASP.NET Core) |
| AI | Chat 管道/工具注册 | M.E.AI · OpenAI · Polly |
| AI.Approval | 高危工具审批门 | FreeSql(队列持久化) |
| AI.SemanticCache | 语义缓存 | — |
| AI.Workflow | 工作流/挂起恢复 | — |
| Auth | 登录/令牌/限流 | 无 |
| Cache | 双层缓存/幂等 | StackExchange.Redis |
| Data.FreeSql | ORM 约定/多库/软删/审计 | FreeSql |
| Events | 进程内事件总线 | 无 |
| Worker | Cron/间隔作业 | Cronos |
| Toolbox | Excel/文件/邮件/脱敏 | MiniExcel |
| Host | 一行总装 | 引上面全部 |

---

## 项目状态

**已发布 [NuGet.org](https://www.nuget.org/packages?q=NexMesh)(0.0.4)**:`dotnet add package NexMesh.Host` 即用。

### ✅ 已交付(22 项能力)

<details>
<summary>点击展开完整清单</summary>

| # | 能力 | 包 |
|---|---|---|
| 1 | 一行总装 | Host |
| 2 | 约定注入 | Core |
| 3 | 契约端点引擎(接口即 API,完整参数绑定) | Core |
| 4 | 虚拟网关(10M+ 路由) | Core |
| 5 | 上下文显式绑定 | Core |
| 6 | 三层防御 | Core + AI |
| 7 | 认证 + 登录限流 | Auth |
| 8 | 授权 + 默认拒绝 | Core + Auth |
| 9 | AI Chat 管道 | AI |
| 10 | AI 工具(静态+实例) | AI |
| 11 | 审批门 | AI.Approval |
| 12 | 语义缓存 | AI.SemanticCache |
| 13 | ToolSearch 语义召回 | AI |
| 14 | 工作流 + 人工介入恢复 | AI.Workflow |
| 15 | 数据层约定 | Data.FreeSql |
| 16 | 双层缓存 | Cache |
| 17 | 定时调度 | Worker |
| 18 | 工具箱 | Toolbox |
| 19 | 脚手架模板 | templates |
| 20 | 进程内事件总线 | Events |
| 21 | 依赖规则测试 + 公共 API 基线 | tests/NexMesh.ArchTests |
| 22 | 性能基准 | benchmarks/NexMesh.Benchmarks |

</details>

### 🔄 进行中 / 路线图

| 项 | 状态 |
|---|---|
| CI/CD 适配 | 进行中 |
| OIDC 单点登录 / gRPC / 数据库 SQL 迁移 | 规划中 |
| Blazor 管理界面 | 低优先级 |

质量保障机制(测试/公共 API 基线/性能基准)见 [框架质量保障](docs/框架质量契约.md)。

---

## 文档

| 文档 | 你会学到 |
|---|---|
| [快速开始](docs/快速开始.md) | 从零 15 分钟跑通(新手从这里开始) |
| [包实战案例](docs/包实战案例.md) | 12 个包逐个上手,每个包一份分步骤案例 |
| [大型项目指南](docs/大型项目指南.md) | 项目变大后的分层/多库/多租户/上线清单 |
| [配置参考](docs/配置参考.md) | 全部配置节 + 可复制模板 |
| [错误码参考](docs/错误码参考.md) | 40xxx/50xxx 清单 + 出错怎么办 |
| [安全规范](docs/安全规范.md) | 安全架构 + 审批门 + 租户隔离 |
| [架构白皮书](docs/架构白皮书.md) | 四层架构 + 包依赖 + 路线图(进阶) |
| [框架质量保障](docs/框架质量契约.md) | 测试/基线/基准机制(进阶) |

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
| 测试 | xUnit 2.9(211 项 + 依赖规则/API 基线/性能基准) |

---

## License

[MIT](LICENSE)
