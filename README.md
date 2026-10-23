<div align="center">

# NexMesh v2.0-ai

**海量 CRUD 与重度 AI 融合的 .NET 10 企业级框架（Green-field 重构版）**

.NET 10 Minimal API · Microsoft.Extensions.AI · FreeSql · Redis (Redis Stack) · Agent Framework

</div>

---

> 本分支（v2.0-ai）为白皮书审核通过后的从零重建版本，旧 11 包生态保留在 master（维护模式，存量项目继续使用 0.0.111）。

## Phase 1（当前）交付

| 包 | 能力 |
|---|---|
| **NexMesh.Host** | **一行宿主总装**：`AddNexMeshService()` / `UseNexMeshService()`——配置/约定注入/FreeSql(配置驱动,缺省 SQLite)/AI 底座/中间件管道全部收口,业务开发者零 DI、零生命周期、零配置代码 |
| **NexMesh.Core** | 约定注入（IScopedService/ISingletonService/ITransientService）、AutoApi（接口即 API：[Http*] 动词 + 路由/Query/Body 有界参数绑定，多复杂参数启动阻断）、ApiResult/PagedData、MeshException + 全局异常中间件、AddNexMeshConfig（Configs/ 多 json + `${ENV\|默认}` 占位符 + 热更）、健康检查/CORS/Scalar |
| **NexMesh.AI** | AddKeyedChatClient 管道（断连熔断守卫/**Polly 弹性管道**/日志/遥测/函数调用，openai-compatible 覆盖 OpenAI/DeepSeek/Ollama + stub 离线开发）、[NexMeshTool] 工具注册（静态+实例一等公民；**High 风险审批门挂起拦截**；参数错配自纠错）、gen_ai 遥测、MaxToolsPerRequest、上下文快照/再水化 |
| **NexMesh.AI.Approval** | **审批门**：High 工具挂起拦截（终局挂起语义+幂等键）、审批队列持久化（Memory/FreeSql，上下文快照随队落地）、批准后快照再水化执行（BackgroundRunner 五步纪律）、审批中心端点 |
| **NexMesh.Data.FreeSql** | **实体约定接口自动接管**：`ISoftDelete`（全局过滤+软删）/`IAudited`（时间审计）/`IUserAudited`（操作人）/`IHasConcurrencyStamp`（乐观并发→409）/雪花 ID（自研 41+10+12）；多库命名注册 `AddNexMeshDb` + `IRepository<T>` 仓储 + 分页 |
| **NexMesh.Cache** | **双层缓存**：L1(Memory)+L2(StackExchange.Redis)；单飞防击穿(双检查)、分布式锁(SET NX+Lua 令牌)、幂等防重(Idempotency-Key→409)、Auto 模式无连接串内存兜底 |
| **NexMesh.Worker** | **定时调度**（替代 Hangfire 轻量自研）：Cron/Interval 双基类一行注册；单轮异常隔离不停摆/退避等待/优雅停机/系统身份快照经 BackgroundRunner 执行 |
| **NexMesh.Toolbox** | **工具箱**：MiniExcel 导入导出、`IFileStorage` 文件存储（Db Blob/本地磁盘/OSS 同接口）、SMTP 邮件、数据脱敏（手机/身份证/邮箱/姓名） |
| **NexMesh.AI.SemanticCache** | **语义缓存**：嵌入向量→余弦相似命中→秒回缓存（成本降 50%+）；内存暴力/Redis FT.SEARCH 双实现；ChatClient 管道装饰器 |
| **NexMesh.Auth** | **不透明 Token 认证**：登录/登出/me 端点 + ILoginValidator 业务扩展点、滑动续期（最大寿命上限）、会话吊销、无效凭据一律 401、AutoApi `[Authorize]` 标注自动转译、审批中心授权收紧 |
| **NexMesh.Core（网关）** | **虚拟分发网关** `MapAutoApiGateway`：单条通配物理路由 + 逻辑路由表（精确字典+参数化模板），10M+ 逻辑路由与 ASP.NET Core 路由树解耦，404/405 业务码 |

## 安全红线（Phase 1）

- High 风险工具发现即抛 `[NexMesh Security Gate]` 异常阻断启动——审批门 Phase 2 交付。
- NexMesh.AI 核心层零引用 FreeSql/EFCore/Redis（有架构约束测试固化）。

## 快速开始

```bash
dotnet run --project samples/NexMesh.AiDemo   # SQLite 零配置,内置 Stub LLM
```

## 文档

| 文档 | 入口 |
|---|---|
| 白皮书审核报告 | [docs/审核-NexMesh.AI白皮书-v1.0.0.md](docs/审核-NexMesh.AI白皮书-v1.0.0.md) |
| v2.0 架构白皮书 | [docs/架构-NexMesh.AI-v2.0.md](docs/架构-NexMesh.AI-v2.0.md) |

## License

[MIT](LICENSE)
