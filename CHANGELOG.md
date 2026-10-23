# 更新日志

本文件格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/),版本遵循 [SemVer](https://semver.org/lang/zh-CN/)。

## [0.2.0] - 2026-10-20 (P1 完成:核心包全量可用)

### 新增(W2)

- `NexMesh.Core`:`NexMeshOptions` 全局选项(`NexMesh` 节,启动即验证)、`ExposeInternalErrors` 500 明细开关、`UseNexMeshTraceId()`(X-Trace-Id 响应头)、`AddNexMeshRateLimiter()`(固定窗口限流,IP 分区 + 反代头,超限 429)
- `NexMesh.Web.Scalar` 首个可用版:`AddNexMeshDocs()` + `MapNexMeshDocs()` 一行启用 OpenAPI 与 Scalar

### 新增(W3)

- Core:ORM 无关共享抽象 `ISoftDelete` / `IAudited` / `PagedRequest` / `PagedResult<T>`
- `NexMesh.Data.FreeSql`:`AddNexMeshDb` 多数据源/主从、软删除全局过滤、审计自动维护、薄仓储 `IRepository<T>`、分页(页大小上限 500)与排序白名单(40010)
- `NexMesh.Cache.FreeRedis`:Auto/Redis/Memory 三模式;未配置 Redis 自动降级内存;连接故障降级 60s 自动恢复;`GetOrCreateAsync` 同 key 单飞防击穿;`TryLockAsync` 分布式锁

### 新增(W4)

- Core:`ICurrentUser`(长短 claim 兼容)
- `NexMesh.Auth`:`AddNexMeshJwt`(HS256 签发/校验 + `ITokenService`)、`AddNexMeshApiKey`(恒定时间比较);claim 契约固定短名(sub/name/role),关闭入站映射保证跨版本确定
- `NexMesh.Events.CAP`:统一 `INexMeshEventPublisher`;Memory 模式进程内直派(处理器异常隔离,零基础设施);RabbitMQ 模式 CAP + Outbox

### 新增(W5)

- `NexMesh.Observability`:`AddNexMeshObservability` 一行接线;Serilog 默认零配置;OTel Traces/Metrics 显式开启;配置 OtlpEndpoint 后日志(Serilog OTel Sink)+链路+指标三信号统一 OTLP
- `NexMesh.Resilience`:官方标准弹性管线全局接线(重试/熔断/超时)
- `NexMesh.Mapping.Mapster`:`IMapper` DI + 集中配置约定
- `NexMesh.Toolbox`:雪花(Yitter,WorkerId 0-63)/UUIDv7、脱敏(手机/身份证/邮箱/姓名)、MiniExcel 封装

### 新增(W6)

- ReferenceService 升级为全栈参考实现:FreeSql 持久化 + 缓存 + JWT + 事件 + 可观测 + 分页 + 排序白名单,端到端验证 8 项全过
- `Dockerfile` 多阶段构建、`docs/部署指南.md`(探针映射/优雅关闭/配置清单)

### 变更

- **目标框架改为仅 net10.0**(原 net8.0+net10.0 双目标),不背多版本包袱
- ReferenceService 与 dotnet new 模板改用 `NexMesh.Web.Scalar`(dogfood)

### 测试

- 45 个测试覆盖全部包(Core 7、Web.Scalar 3、Data 6、Cache 7、Auth 5、Events 4、Toolbox 9、集成冒烟 4,Release 全绿)

## [0.1.0] - 2026-10-20 (P0 骨架)

### 新增

- 仓库骨架:解决方案、集中包版本管理(CPM)、仓库级 NuGet 单源、CI 流水线
- `NexMesh.Core` 首个可用版本:`ApiResult`/`ApiResult<T>`、`MeshException`、全局异常中间件、`AddNexMesh()`、`MapDefaultEndpoints()`(/health、/health/ready)
- `NexMesh.Core.Tests` 4 个集成测试
- `samples/ReferenceService`:FastEndpoints + Scalar 的 Orders CRUD 参考实现
- `templates/NexMesh.Templates`:`dotnet new nexmesh-service` 脚手架
- 其余 9 个包占位壳,P1 逐周实现

## [未发布]

### 文档

- 全仓文档叙事统一为独立框架(从零构建),明确技术基座定位:核心包基于原生 ASP.NET Core 抽象(不绑定 API 风格),端点层默认 FastEndpoints
- 《设计决策与路线图》(docs/设计决策与路线图.md):技术基座对比、已定决策表、版本路线
- 开发规范扩至 17 篇:总览/分层/契约/校验/错误码/ID/事务/缓存/认证/事件/数据访问/可观测/配置/异步/部署/上架清单/数据库迁移
- `llms.txt`:AI-first 文档索引

## [0.3.0] - 2026-10-20 (生产实战强化)

### 变更(破坏性)

- `PagedResult<T>` 字段更名:`Total→TotalCount`、`Page→PageIndex`(通用分页命名,契约对外稳定)

### 新增

- `NexMesh.Data.Migrations`(新包):SQL 脚本迁移运行器——V001__描述.sql 命名、文件目录/嵌入资源、`__nexmesh_migrations` 版本表、幂等重跑、失败快速终止。确立"SQL 脚本是生产迁移唯一权威,AutoSyncStructure 仅开发期"立场
- `IRepository.InsertOrUpdateAsync`:按主键 Upsert,替代"查出→逐字段拷贝→Update"样板
- Core 标记接口注册:`ITransientService`/`IScopedService`/`ISingletonService` + `AddNexMeshServices(assembly)`——扫描便利,但注册行显式可见(显式优于约定)
- 规范 16-数据库迁移规范:SQL 脚本权威立场、AutoSyncStructure 边界、部分唯一索引(软删除共存)实战条款;02/10 同步更新

## [0.4.0] - 2026-10-20 (R2 生产加固:独立审核修复)

> 依据全仓独立代码审核(并发/安全/契约/可靠性四维),修复全部 P0/P1 及 7 项 P2。66 测试。

### 修复(P0:Redis 缓存路径不可上生产问题)

- 分布式锁:争用超时不再包装成"假锁"(原必然 NRE 且互斥失效);改立即尝试(wait=0)、真实 TTL 过期、关闭看门狗续期
- GetOrCreateAsync:争锁失败改为有界等待重读(≤300ms),防击穿承诺在争用下仍成立
- Redis 全路径改异步 API(GetAsync/SetAsync/DelAsync),不再同步阻塞请求线程

### 修复(P1:契约与合规)

- `ttl = null` 契约兑现:两实现均"永不过期"(原静默 1 小时过期)
- 审计操作人真正自动填充:CreatedBy/UpdatedBy 经 IHttpContextAccessor 取当前用户(短名/长名 claim 兼容),后台任务留空
- 降级误判:仅连接类故障触发降级;脏数据反序列化失败按 miss 处理并清理坏 key,不再雪崩整源
- 限流反代头默认不信任:新增 `TrustProxyHeader` 显式开关(默认 false),防 X-Forwarded-For 伪造绕过限流

### 修复(P2)

- 哈希取模 int.MinValue 溢出;内存缓存键加源前缀隔离(多源不串数据);迁移版本表名注册期白名单校验;分页偏移 long 计算防溢出;脱敏非标长度兜底遮蔽(+86 前缀不再整串明文);事件未识别 Transport/Storage 启动即失败(防静默降级丢事件);业务异常补 Debug 日志(撞库/枚举可观测)

## [0.5.0] - 2026-10-20 (R3 安全与语义收尾 + 真实 Redis 验证)

### 安全

- ApiKey:改为 SHA-256 摘要恒定时间比较(消除长度时序侧信道);新增具名映射 `NexMesh:Auth:ApiKeyMap`(客户端Id→密钥),身份进 NameIdentifier claim,调用方可区分、可单独吊销
- API 文档端点生产默认不暴露:/scalar、/swagger 需 `NexMesh:Docs:EnableInProduction=true` 显式开启(接口结构即情报)

### 修复与语义

- Redis 分布式锁重写为 SETNX + TTL + Lua 安全释放(仅删自己的 token):真实硬过期(不依赖看门狗)、释放不影响他人持有的锁;通过真实 Redis 实例验证(互斥/TTL 过期/争锁有界等待)
- 雪花 ID:未初始化直接抛错(多实例默认 WorkerId 会撞主键);重复 AddNexMeshIds 抛错
- JWT 签发与校验共享同一配置快照(消除热更窗口的"新签旧校验")
- 标记接口多生命周期标注抛错(不再静默取第一个);跳过开放泛型
- ICacheLock ttl 语义文档化(Redis 硬过期 / 内存进程内)

### 测试

- 新增真实 Redis 集成测试套件(NEXMESH_TEST_REDIS 环境变量门控,默认跳过);ApiKey 身份、文档门禁回归;74 测试全绿

## [0.6.0] - 2026-10-20 (R5 第二轮复审修复)

> 第二轮独立复审(修复验证 + 新问题扫描)后修复:P0×1、P1×5、P2×10。

### 修复(P0/P1)

- 缓存值类型命中判定重写:GetOrCreate/Get 基于原始 payload 判命中(修复值类型恒判"命中"返回 default 且永不执行 factory);真实 Redis 路径新增值类型回归测试
- 降级读写类型对称:降级窗口内存写序列化 payload(修复非 string 类型降级后恒 miss 引发回源风暴)
- 内存锁改固定分片信号量(消除释放-清理竞态导致的双持锁);严格互斥场景明确要求 Redis 模式,降级期锁语义退化显式告警
- 限流 XFF 取链尾(可信代理追加侧)+ IP 格式校验,链首伪造不再能翻新分区键
- 迁移排序按 V{n}__ 版本号(修复文件名与嵌入资源全名的字典序错乱);版本号回归测试
- 规范 06 事务示例重写为真实 UnitOfWork API(原示例不可编译),并附提交/回滚双测试;数据包引入 FreeSql.DbContext 开箱支持
- 分布式锁键独立 lock: 命名空间(不与数据键互踩);TTL≤0 抛参错(防"永不释放")

### 修复(P2 摘要)

- 限流 429 响应统一 ApiResult;JWT ExpiryMinutes 正数校验;审计 CreatedAt 仅默认值时填充(保留导入数据原始时间);审计 claim 优先级与 ICurrentUser 统一;IFreeSql 随容器释放;WorkerId 0-63 校验 + AddNexMeshIds(IConfiguration) 重载;Excel Stream 重载物化;事件处理器取消不再记 Error;服务扫描 System/System.* 双规则过滤;反序列化 NotSupportedException 按 miss
- CI 增加 Redis service,真实 Redis 集成测试进流水线

## [0.6.1] - 2026-10-20 (终审收尾)

- 终审结论:全部 P0/P1 修复正确、无新引入缺陷,达到生产准入线
- WorkerId 配置解析失败启动即抛错(原静默取 0 有多实例撞号风险);规范 05 键名同步为 NexMesh__Ids__WorkerId
- 规范 16 声明迁移器方言边界(已验证 PostgreSQL/SQLite;SQL Server/MySQL 需自行验证)

## [0.7.0] - 2026-10-20 (ABP 稳定性对标:六项企业级能力)

### 新增(Core)

- `IHasConcurrencyStamp`:乐观并发标记——仓储 UpdateAsync 自动比较戳,不匹配抛 409(40902),防并发覆盖写
- `ICurrentTenant` / `CurrentTenant`(AsyncLocal):跨 await 租户上下文,`Change(id)` 嵌套还原
- 审计中间件 `UseNexMeshAudit()`:动作级审计(方法/路径/状态/耗时/用户/租户/TraceId),慢请求升级 Warning,IAuditLogStore 持久化扩展点
- 周期后台工作者 `NexMeshPeriodicWorker`:独立 Scope、异常隔离(不因单轮失败崩日志)、优雅退出
- `ITenantEntity` 多租户标记(配合 Data 包过滤)

### 新增(Data.FreeSql)

- 乐观并发 CAS:UpdateAsync 内自动比戳+换戳(带完整测试)
- 多租户过滤 `EnableTenantFilter`:查询自动限定当前租户(AsyncLocal),宿主(null 租户)可看全部
- 种子 `IDataSeedContributor` + `AddNexMeshSeed<T>` + `ApplyNexMeshSeeds()`:启动时预置字典/初始数据

### 新增(Cache.FreeRedis)

- 幂等防重 `UseNexMeshIdempotency()`:同 Idempotency-Key 并发写请求只放一个,其余 409(40903)

### 测试

- 新增 14 项测试:并发冲突/租户隔离与嵌套还原/审计捕获与用户租户关联/种子幂等/幂等防重(同键 409、异键通、无键通)/周期工作者多轮执行
- 全仓 138 测试 Release 全绿(此前 125)

## [0.8.0] - 2026-10-20 (SSS+ 冲刺:全部 P2 清零)

### 修复(P2 清零)

- **锁全异步**:`SetNxAsync` / `EvalAsync` / `DelAsync` 替换同步调用(此前 SETNX/Eval/Del 为同步,高并发占线程池);锁释放 `DisposeAsync` 真异步(不再同步阻塞等待 Lua 完成)
- **401 响应体统一**:`AddNexMeshJwt` 的 `OnChallenge` 返回 ApiResult JSON(`{"success":false,"code":40101,...}`),不再空体
- **脏 payload + 值类型假命中修复**:`TryDeserialize` 返回 `(bool, T?)` 二元组,反序列化失败(含值类型)正确按 miss 处理(此前值类型脏数据返回 default 被误判为命中)
- **限流分区有界化**:分区键哈希到固定 10 万槽(`BoundedKey`),海量不同 IPv6/伪造键不撑爆限流器字典内存

### 验证

- 138 测试 Release 全绿(含 Core 58 + Cache 16 含幂等 4 + Data 16 含并发/租户/种子)
- 真实 Redis 7/7 全绿(含锁互斥/TTL 硬过期/单飞/值类型)

## [0.9.0] - 2026-10-20 (SSS+ 第二轮:审计P0×2 + P1×6 修复)

### P0 修复

- **AuditMiddleware**:ICurrentUser/ICurrentTenant 从构造函数注入改为 InvokeAsync 参数注入——修复 Development 环境作用域校验启动崩溃;NexMeshAuditOptions.Enabled 真正生效(原先为死配置)
- **乐观并发**:InsertOrUpdateAsync/UpdateManyAsync 同步走并发校验(此前绕过戳检查)

### P1 修复

- 401 响应体带真实 TraceId(原先硬编码 null)
- 幂等防重错误码从 40903 统一为 40901(消除段位冲突)
- Cache README TTL 措辞修正(null=永不过期,非"缺省1小时")
- 测试计数三处统一为 138;版本引用同步 v0.9.0;移除 dummy 目录残留
- CHANGELOG 修正 NexMeshPeriodicWorkerBase→NexMeshPeriodicWorker;[未发布]节移至顶部

## [1.0.0] - 2026-10-20 (生产就绪首发)

### 新增

- **性能基准套件**(BenchmarkDotNet):裸 ASP.NET Core vs NexMesh 全中间件、缓存命中、ApiResult 序列化、PagedResult、脱敏
  - 全中间件开销 **~23%**(TraceId+审计+异常处理三件套,~18μs/请求),企业可接受
  - 缓存命中 **60ns**(内存模式);ApiResult 序列化 **119ns**;脱敏 **3ns**
- **API 版本控制**:UseNexMeshApiVersion()(X-Api-Version 头/查询参数,头声明+URL 段版本路由)
- **docs/errorcodes.md**:错误码登记表(框架保留码 + 段位规则 + 服务自定义区)

### 变更

- 版本号从 0.x 跃升至 1.0.0(生产就绪首发)
- 11 个 NuGet 包全部打包就绪(EmbedAllSources、符号包、统一元数据)

## [1.1.0] - 2026-10-20 (运维友好:可观测性升级)

### 新增

- **NexMeshDiagnostics 装配登记簿**:每个 AddNexMesh* 注册时登记,启动后输出结构化摘要日志(哪些包生效/什么模式/什么开关,一目了然)
- **/diagnostics 运行时端点**:JSON 格式实时展示框架版本/环境/运行时长/装配清单/请求指标(路由级计数/P95/错误率)
- **请求指标中间件**:RequestMetricsMiddleware 自动记录每请求的方法/路径/状态/耗时,NexMeshRequestMetrics 提供 GetSnapshot()
- **各包诊断登记**:Core/Data.FreeSql/Cache.FreeRedis/Auth.JWT/Events.CAP/Observability 全部在注册时自动登记到装配簿

### 运维价值

- 启动日志一行看全:`[NexMesh] 装配摘要:6 项能力生效` + 逐项详情
- /diagnostics 实时可查:不用看代码就能知道"这个服务启了什么"
- 请求指标:`GET /api/orders` → `{count: 1234, errors: 5, p95ms: 45.2}`,性能瓶颈一眼定位

## [1.1.1] - 2026-10-20

- long 类型主键自动生成雪花 ID(AddAsync/InsertOrUpdateAsync,Id==0 时自动填)
- Data.FreeSql 引入 Yitter.IdGenerator(懒初始化,不覆盖 AddNexMeshIds 的自定义 WorkerId)
- 7 项雪花 ID 全链路测试(自增/唯一/递增/查找/更新/软删/分页/Upsert)
- README 重写:实例代码导向,去 AI 味;155 测试全绿

## [1.2.0] - 2026-10-20 (自动 API + 文档同步)

### 新增

- **自动 API(IAutoApi)**:接口+实现类自动注册 DI + 自动映射 REST 端点
  - 标准 [HttpGet("{id}")] [HttpPost] 注解指定路由(ASP.NET Core 原生特性)
  - 无注解默认 GET,方法名做子路由(kebab-case)
  - 参数绑定:简单类型→路由/查询,复杂类型→JSON Body
  - 响应自动包装为 ApiResult<T> 泛型(完整五字段)
  - 不需要继承 Endpoint/Controller 任何父类
- API 风格升级为四种:FastEndpoints / 自动 API / Minimal API / MVC Controller
- Body 绑定修复:ContentLength 为 null(TestServer/chunked)时仍读 JSON

### 文档同步

- README:自动 API 完整示例(注解用法/路由规则表/参数绑定表/响应格式)
- Core README:新增自动 API 章节
- 故障排查:API 风格决策表补"自动 API"行
- llms.txt:Core 能力清单补自动 API/ICurrentTenant/IHasConcurrencyStamp 等
