# 更新日志

## v0.0.4 (2026-10-23)

Scalar 契约文档自生成器（自 v0.0.3 起 20 个提交）。

- **契约端点 OpenAPI 3.0.3 文档直出** `/openapi/v1.json`：反射式 RequestDelegate 端点不被 .NET 默认生成器收录，由 `ContractOpenApiDocument` 自产（仅依赖 Scalar.AspNetCore 做 UI）
- **字段级 schema**：属性递归展开 + camelCase + XML 中文注释 + 示例值；字典/KeyValuePair 序列收敛 map schema；信封响应 + 400/401/404/500 错误结构
- **IsSimple 单一源**：文档 schema 与参数绑定共用 `AutoApiDispatcher.IsSimple`（11 项边界断言防两套判定分叉）
- 侧栏"中文描述 METHOD /路由"合并展示；tags 短名冲突按接口计数；文档预序列化缓存 + 降级保护
- 移除 `Microsoft.AspNetCore.OpenApi` 依赖

## v0.0.3 (2026-10-23)

**约定对齐升级（约定级破坏变更）**——对外表面与 MicroMesh 完全一致，单一约定、无历史包袱：

- 信封 `ApiResult`：成功 `code=0`；失败 `success=false` + 业务码（前端 H5 依赖 `code=0`）
- 分页 `PagedResult<T>`（Items/TotalCount/PageIndex/PageSize/TotalPages/HasNext/HasPrevious）
- 异常族：`MeshException` + 11 个语义子类（Business/NotFound/Unauthorized/Forbidden/Validation/Duplicate/Remote/Unavailable/Timeout/…）
- 契约端点唯一约定：`[ApiContract]`/`IServiceApi` 接口 + 原生 `[Http*]`/`[Authorize]`，路由 `api/{服务名}/{模板}`；移除 `IAutoApi`/`[ServiceRoute]` 类模式
- 认证表面：不透明令牌（可吊销/滑动续期/Redis 会话）+ `ICurrentUser<TUser>` + `IJwtTokenService`/`ITokenStore`
- 数据表面：`AddDBSource<TDBSource>` 类型化多源 + `IRepository` 标记 + `IIdGenerator`
- 模块宿主 `AddNexMeshHost`/`UseNexMeshHost`；SocialWork 全量迁移验证（159 端点路由 100% 对账）

## v0.0.2 (2026-10-23)

修复 NuGet 发布认证（API key）。

## v0.0.1 (2026-10-22)

从零重建（Green-field），基于白皮书审核结论 + 双轮 SSS+ 安全审查。

### 11 个包

- **NexMesh.Host**：一行总装（`AddNexMeshService()` / `UseNexMeshService()`）
- **NexMesh.Core**：约定注入 · 契约端点引擎（接口即 API，完整参数绑定：路由/Query/Body/数组/文件/FromForm）· 虚拟网关（10M+ 逻辑路由）· 上下文显式绑定 · 三层防御
- **NexMesh.AI**：Chat 管道四层（断连熔断/日志/遥测/函数调用）· Polly 弹性 · ToolSearch 语义召回 · 实例工具一等公民
- **NexMesh.AI.Approval**：审批门（High 工具挂起拦截 · 幂等键 · 快照随队 · 批准后再水化执行）
- **NexMesh.AI.SemanticCache**：语义缓存（嵌入向量→余弦命中秒回 · 租户隔离）
- **NexMesh.AI.Workflow**：工作流（步骤序列 · Checkpoint 持久化 · HITL 挂起恢复 · CAS 防并发）
- **NexMesh.Auth**：不透明 Token · 登录限流 · 滑动续期 · 契约授权标注 · 默认拒绝
- **NexMesh.Cache**：双层缓存（L1+L2）· 防击穿 · 分布式锁 · 幂等防重
- **NexMesh.Data.FreeSql**：实体约定自动接管（软删/审计/租户过滤/并发/雪花）· 多库 · 仓储
- **NexMesh.Toolbox**：Excel · 文件 Blob 存储 · 邮件 · 脱敏
- **NexMesh.Worker**：Cron/Interval 调度 · 异常隔离 · 系统身份快照

### 安全体系（SSS+ 双轮审查通过）

- 默认拒绝三件套（契约端点 / 网关 / 审批中心）
- 租户隔离三层闭环（全局过滤 / 写入填充 / 缓存键隔离）
- AI 安全门（High 工具启动阻断 / 启用后挂起拦截 + 终局语义 + 幂等键）
- 三层防御（断连熔断 / 上下文穿帮 / 工具自纠错）
- 登录限流 · 信息泄露防护 · CORS fail-closed · 文档生产门控

### 技术栈

.NET 10 · Microsoft.Extensions.AI 10.11 · FreeSql 3.5 · StackExchange.Redis 3.4 · Polly 8.8 · Cronos 0.13 · MiniExcel

### 测试

178 项全绿 · 零构建警告
