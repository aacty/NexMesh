# 更新日志

## v2.0.0-alpha.1 (2026-10-22)

从零重建（Green-field），基于白皮书审核结论 + 双轮 SSS+ 安全审查。

### 11 个包

- **NexMesh.Host**：一行总装（`AddNexMeshService()` / `UseNexMeshService()`）
- **NexMesh.Core**：约定注入 · AutoApi（接口即 API，完整参数绑定：路由/Query/Body/数组/文件/FromForm）· 虚拟网关（10M+ 逻辑路由）· 上下文显式绑定 · 三层防御
- **NexMesh.AI**：Chat 管道四层（断连熔断/日志/遥测/函数调用）· Polly 弹性 · ToolSearch 语义召回 · 实例工具一等公民
- **NexMesh.AI.Approval**：审批门（High 工具挂起拦截 · 幂等键 · 快照随队 · 批准后再水化执行）
- **NexMesh.AI.SemanticCache**：语义缓存（嵌入向量→余弦命中秒回 · 租户隔离）
- **NexMesh.AI.Workflow**：工作流（步骤序列 · Checkpoint 持久化 · HITL 挂起恢复 · CAS 防并发）
- **NexMesh.Auth**：不透明 Token · 登录限流 · 滑动续期 · AutoApi 授权标注 · 默认拒绝
- **NexMesh.Cache**：双层缓存（L1+L2）· 防击穿 · 分布式锁 · 幂等防重
- **NexMesh.Data.FreeSql**：实体约定自动接管（软删/审计/租户过滤/并发/雪花）· 多库 · 仓储
- **NexMesh.Toolbox**：Excel · 文件 Blob 存储 · 邮件 · 脱敏
- **NexMesh.Worker**：Cron/Interval 调度 · 异常隔离 · 系统身份快照

### 安全体系（SSS+ 双轮审查通过）

- 默认拒绝三件套（AutoApi / 网关 / 审批中心）
- 租户隔离三层闭环（全局过滤 / 写入填充 / 缓存键隔离）
- AI 安全门（High 工具启动阻断 / 启用后挂起拦截 + 终局语义 + 幂等键）
- 三层防御（断连熔断 / 上下文穿帮 / 工具自纠错）
- 登录限流 · 信息泄露防护 · CORS fail-closed · 文档生产门控

### 技术栈

.NET 10 · Microsoft.Extensions.AI 10.11 · FreeSql 3.5 · StackExchange.Redis 3.4 · Polly 8.8 · Cronos 0.13 · MiniExcel

### 测试

129 项全绿 · 零构建警告

---

## v1.x（历史版本）

旧 11 包结构（0.0.111），存量项目继续使用。历史提交已压缩，旧代码见各包 README。
