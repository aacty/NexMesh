# 更新日志

## 未发布(0.0.8)

- **自建 OIDC IdP(框架可当身份中心)**:AddNexMeshOidcIdp/MapNexMeshOidcIdp——授权码+PKCE S256(标准 base64url challenge)/ client_credentials / 发现文档与 JWKS 挂根路径(OIDC 惯例,与 OidcJwksFetcher 拼法对齐)/ RS256+`kid` 签发 / RotateKey 轮换(轮换后 /jwks 自动吐新 kid);声明(sub/preferred_username/tenantid/roles)与 RP 验证器映射一一对应;authorize 校验客户端注册与 redirect_uri,一次性码 90 秒。3 项闭环测试(IdP↔RP 全流程:JWKS 发现→验签→token-exchange 换发本地令牌;码一次性+PKCE 错拒;client_credentials 错凭据 401)。实战纠偏两处:①token 端点曾把授权码兑换返回的 user_id 直接当 access_token 回发(兑换只返回 user_id,端点须查用户存储后签发完整声明);②PKCE 校验曾用 hex 与 base64url 挑战裸字符串比较且测试用同一错误生成法互相掩盖——修正为标准 S256 精确匹配

- **性能基准补齐(HTTP 层之外)**:数据层三项(软删过滤计数 48.9μs/单行查询 58μs/插入 6.9ms——SQLite 文件库 fsync 特性,量级预期内)+缓存三项(L1 命中 48ns/未命中写 135ns/防击穿读 101ns)入 benchmarks 工程;数字入库质量契约。踩坑记录:FreeSql SQLite :memory: 在连接池下每连接独立(改文件库)、官方包无 Release 标记需 DisableOptimizationsValidator、SQLite Provider 须显式引包

- **四大能力补齐(收尾批)**:
  ①缓存键巡视——Cache 包新 ICacheInspection(ICacheFactory 增 SourceNames):Redis SCAN 按前缀列键+TTL(上限500)/单键删除(L1+L2 同清);内存模式诚实空集;Dashboard 缓存面板"巡视键"按钮+键表+删除;真 Redis 测试
  ②迁移在线操作——IMigrationRunner.ListPendingAsync(与写同闸);Dashboard 迁移面板显示待应用清单+"在线执行"按钮(POST 走 RunPendingAsync 完整分布式锁+单迁移事务+fail-fast,与部署同路径不双跑)
  ③gRPC Reflection——MapNexMeshGrpc 内建(Grpc.AspNetCore.Server.Reflection):客户端可动态发现 ContractGateway 服务与方法
  ④OIDC JWKS 在线发现——OidcJwksFetcher(发现文档→jwks_uri→kid→RSA 字典,OIDC 标准 n/e Base64Url);验证器按 header.kid 匹配 JWKS(轮换:重新拉取后 SetJwksKeys 原子替换),无匹配回退静态 PEM;修复 Enabled 门槛 bug(纯 JWKS 模式 Issuer+密钥集即启用——此前要求必须有 Secret/PEM 导致 JWKS 拉取成功也被整体关闭,独立调试程序二分定位);自建 HttpListener IdP 端到端测试(kid 匹配通过/未知 kid 拒绝)
- **服务发现(并入 NexMesh.Cache,非独立包——与缓存同依赖同语义:Redis 共享结构)**:Redis 注册表模式——实例键 TTL+心跳续约(每 TTL/2)优雅停机注销,服务索引惰性剔除(发现时清过期);AddNexMeshServiceDiscovery(连接串, self=>配置)+IServiceRegistry.DiscoverAsync;真 Redis 4 测(多实例并列/注销消失/TTL 过期剔除/心跳跨周期续约+停机注销)

- **Dashboard 操作面审计加固(走查)**:①作业触发失败降级信封(反射 Task 异常不再 500);②JobRunRecord 补 TriggeredBy(操作人随记录,端点经 ICurrentUser 注入;HTTP 访问日志为权威);③Interval 作业失败也记记录(与 Cron 一致,修复吞异常不一致);④同名作业去重语义注释固化
- **Dashboard 操作面交互修复(实机发现)**:操作按钮点击后不重新拉取 overview——旧数据重渲染导致界面无变化(操作实际已生效,属纯前端数据流缺陷);修复为"操作→toast 提示→400ms 后重拉 overview→重渲染标签与面板"闭环;实机验证(触发前后 manual 记录 0→1、踢下线 revoked 计数与提示)
- **Dashboard 操作面(可读+可操作)**:①后台作业立即执行——NexMeshCronJob/IntervalJob 新增 TriggerNowAsync(与调度轮共用执行闸互斥,忙碌立即返回不排队)+ 最近执行记录(环形10条:时间/耗时/成败/来源 scheduled|manual,内存态审计走日志);Dashboard 作业面板每行"立即执行"按钮+执行记录明细;②会话踢下线——ISessionStore 新增 RevokeByUserAsync(默认接口方法,自定义实现者零破坏;内存/Redis 双实现,Redis 走 SCAN 删除);会话面板每行"踢下线"按钮(吊销该用户全部会话,旧 token 立即失效)。明确不做并注释:批量批准/迁移在线触发/缓存清空(防击穿)/Cron 热改(走部署)。测试:Worker 7(热执行/忙碌互斥/失败记录)、Dashboard 13(触发端点/踢下线 401 失效链)、Auth 33+2(内存/真Redis 按用户吊销)

- **实战审查追加发现并修复:AI 面板风险徽章映射缺陷**——AIFunction 名去 Async 后缀而风险表按方法名登记,High 工具在面板显示为 Low 默认值(AiDemo 实机发现:ForceCloseOrder 标 High 显示 Low;安全展示级缺陷,审批门实际判定不受影响);修复双键登记(带/不带 Async)+ 回归测试(FakeToolRegistry+HostAssemblySet 显式注入——TestServer 下 EntryAssembly 是 testhost 的附带发现);修复过程中框架安全门再次实证:测试程序集含 High 夹具且未开审批门→全部宿主启动失败(fail-fast 纪律有效)
- **Dashboard 实战审查(13/13 真实执行,程序入库 benchmarks/NexMesh.DashboardWarTests 可复跑)**:审批实弹(ApprovalGate 真实入队幂等合并→端点批准→Failed 终态);HTTP 200 并发×400 请求 overview 七面板聚合零失败(1.3万 req/s);Redis 真机双实例共享会话(各登录互见 5,吊销实时反映);1100 会话 SCAN 封顶 1000(140ms);离线 Redis 底层实测 RedisConnectionException;端点级面板异常 HTTP 200 降级。实战纠偏三处:SE.Redis 3.x 连接串 abortConnect(非 abortOnConnectFail)、军演跨进程撞共享登录限流(实证多实例全局限流设计)、断言从异常类型改为端到端行为级

- **SSS+ 审查(三新面板批次)**:①ListAppliedAsync 与迁移写路径共享信号量——面板查询绝不吐 Approved 未 Executed 中间态/建表 DDL 不撞写事务(200 轮并发观察专项测试:只见 0/1 终态);②main 源记录即使迁移类已卸载仍展示(源清单=注册源∪main);③内存会话枚举快照语义注释固化;④Redis SCAN 取消/上限/主节点语义复核关闭
- **Dashboard 三新面板(能力探测同机制)**:数据库迁移(IMigrationRunner.ListAppliedAsync 新查询接口,按源列已应用记录/耗时;库不可达源降级空)、缓存(经配置节读模式与源清单——零新契约;ConnectionString 不回显)、会话(ISessionStore 新增默认接口方法 ListActiveAsync——自定义实现者不受影响;内存/Redis 双实现;Token 仅前缀打码,Redis 走 IServer SCAN 主节点限 1000 条)。前端三张表随面板数据自动出现;5 项面板测试 + 2 项真 Redis 会话枚举实证(打码/吊销消失/过期不落)

- **框架管理台 NexMesh Dashboard(并入 Host,零新包)**:能力探测驱动——overview 聚合端点逐面板探测容器(AI/审批门/后台作业/事件总线),启用了什么对应面板自动出现;加面板=实现 IDashboardPanel,前端(内嵌单页,原生 JS)数据驱动渲染零改动。静态页匿名可载+登录;审批中心批准/拒绝转发 ApprovalDecisionService(decidedBy=ICurrentUser,枚举投影字符串);AI Provider 不回 ApiKey;工具 Risk 徽章(反射扫 [NexMeshTool]);默认仅 Development。启动时钟由两宿主总装自动注册(修复 Map 期对已 build 容器 GetRequiredService 崩溃)。5 项测试 + AiDemo 实机验证(静态页 200/401 信封/登录后 4 面板亮起/AI Risk/作业清单/队列深度)

## v0.0.7 (2026-10-25)

迁移分布式锁 + OIDC 外部 IdP 对接 + 契约 gRPC 面(三线并行)。

- **迁移分布式锁(Redis 租约)**:补全 0.0.6 明示的"单实例执行"约束——IMigrationLock 抽象(SET NX PX 随机 token/半租约 Lua 续期/释放校验 token;崩溃后租约过期可接管无死锁);容器有 IConnectionMultiplexer 自动启用(运行期解析,注册顺序无关),无 Redis 时 Noop 锁语义不变;Runner 拿不到锁等待重试(90s 超时)。4 项真 Redis 测试(互斥/崩溃接管/续期保活/双 Runner 并发恰好一次)
- **OIDC 外部 IdP 对接(token-exchange)**:自研轻量 JWT 验证器(零外部依赖:HS256/RS256 验签 + exp/nbf/iss/aud + 时钟偏移);配置 NexMesh:Authentication:Oidc 启用;登录端点收 oidcToken 换发本地不透明令牌——滑动续期/吊销/ICurrentUser 全保留。模糊输入实测修复安全解码缺口;6 项测试(自造 RSA/HMAC JWT 全语义)
- **gRPC 网关千万级压测**:10,000,000 请求/48 通道(InMemory),ok=10M err=0 逐笔载荷校验全对,吞吐 465,159 req/s,峰值堆 22MB 零增长;压测程序入库 benchmarks/NexMesh.GrpcLoadTests(教训:Info 级请求日志在千万级下拖死 IO,压测须 LogLevel.Warning)
- **全面审核加固(0.0.7 三线)**:①迁移锁续期循环容错——Redis 闪断不再打死循环(退出=续期停止=租约过期=双实例并发迁移,分布式锁核心失效模式;改为失败续跑仅取消退出);②OIDC HS256 弱密钥(<32 字节)拒绝启用(防字典攻击);③exp/nbf 越界数值钳制+减法方向比较(MaxValue±skew 溢出,补丁测试当场抓获);④gRPC 网关 (service,method)→端点 惰性缓存(原每请求全量反射扫描);⑤Benchmarks TryPassAsync 对齐
- **契约 gRPC 面(通用网关)**:AddNexMeshGrpc/MapNexMeshGrpc;单 proto(ContractCall/ContractReply 永不变)+按 (service,method,args_json) 路由到与 REST 完全相同的契约实现——业务契约零 proto 生成;响应 REST 同款信封;NexMesh:GrpcPort>0 自动 Http2 监听;Host 包 Both 生成,消费方引包即得 client。双面一致集成测试

## 未发布(0.0.6)

新包 NexMesh.Data.Migrations(数据库版本化迁移)+ 登录限流 Redis 化 + 契约端点限流 + HTTP 压测基线。公共 API 变化见 tests/NexMesh.ArchTests/ApiBaselines 基线 diff(Core 新增 AddNexMeshRateLimiter/AutoApiEndpoint.RateLimitPolicy;Auth 的 LoginRateLimiter.TryPass 改为 TryPassAsync 并新增计数器抽象;新包 Migrations 首次建立基线)。

- **新包 NexMesh.Data.Migrations**:显式迁移链(升序/单迁移单事务/幂等/Checksum 防篡改/多数据源按 Source 各自独立/RevertAsync 回退);显式 app.MigrateNexMeshAsync() 或 Enabled=true 宿主自动执行(默认关闭)。8 项语义测试(SQLite 实测含事务回滚)
- **登录限流多实例**:`ILoginRateLimitCounter` 抽象 + 内存实现 + Redis 实现(Lua 原子 INCR+PEXPIRE);配置 Store=Auto(有认证 Redis 即全局计数)/Memory/Redis、RedisFailMode=Open(默认)/Closed;本地 Redis 实测并发 20 恰好放行限额
- **契约端点限流**:`AddNexMeshRateLimiter`(配置驱动命名策略 Ip/User 分区,统一 429 信封;configure 回调支持代码级策略);契约方法标 [EnableRateLimiting("策略名")] 生效(方法级;官方特性不支持接口标注);两宿主管线带标记守护挂 UseRateLimiter + 启动期未知策略名告警
- **HTTP 压测基线**:benchmarks/NexMesh.LoadTests(零依赖);首次实测入库(AiDemo/SQLite:health 116k RPS p99 1.3ms;契约分页 7.2k RPS p99 66ms,0 错误)
- AiDemo:限流演示端点(Configs/RateLimit.json);文档:配置参考三节/包实战案例第 13 节/大型项目指南清单更新
- **修复:宿主管线限流插入丢失**(管线曾因脚本补丁静默失败未挂 UseRateLimiter,单元测试因自挂中间件而全绿、掩盖缺口;端到端复测发现 5 连 200)。修复后 UseNexMeshService/UseNexMeshHost 经 UseNexMeshRateLimiting(标记守护+审计)真实挂载;新增宿主管线级测试固化该端到端断言;AiDemo 实测 200×3→429×2。publish.yml 版本号改从 tag 名取(去掉 v 前缀),发版号与 tag 一致
- **百亿级终极审计完成(10,000,000,000 次打击,66 分钟)+ 溢出缺陷修复**:审计推演发现 Window.Count 为 int——单窗口累计 2,147,483,647 次后溢出为负,限流恒放行;修复为 long 并实证:21.4 亿次打击限额 3 放行恰好 3。100 亿次交错计数:放行 2,147,483,647(精确=限额)+ 拒绝 7,852,516,353(精确=余量),零丢失零超发,322 万 ops/s,峰值堆 3MB 零增长;Redis 路径 10 万次真实网络随机并发,500 Key 恰好各放行限额数
- **千万级并发极限审计(10,000,000 操作,24 核/96 并行度,报告入库 benchmarks/NexMesh.ConcurrencyAudit 可复跑)**:登录限流计数器 10M 交错守恒 PASS(310 万 ops/s);事件总线有界 Channel 10M 写零丢失 PASS(225 万 ops/s,回压下);100 万独立 IP 分区隔离恰好 300 万放行 PASS。审计发现并修复:内存限流计数器过期 Key 无清扫(实测 100 万 Key 驻留 141MB 且永不释放,慢速假 IP 扫描内存无限增长)——新增摊销清扫(每 4096 次调用触发,双窗口保守阈值),修复后 134.6MB 注入过期清扫全量回收;补 2 项清扫语义测试(FakeTime 拨钟 + 10 万并发计数守恒)
- **SSS+ 评审(0.0.6 新代码)**:①Revert 未应用迁移照跑 DownAsync 的误删风险——先查记录不存在即拒绝(新增 2 项测试);②应用循环逐条间补取消令牌检查(停机取消即时生效);③ApplyAsync 虚假异步修正为同步方法(事务边界内 GetResult 为既定语义,注释固化);④DurationMs 改为含记录写入的真实耗时;⑤多实例部署语义明示——迁移为单实例执行(进程内信号量不跨进程),滚动部署先迁后扩,分布式锁列后续
- **审核加固**:迁移 Runner 并发守卫(信号量串行化,防显式调用与宿主自动执行叠加双应用)+ 按次作用域解析迁移实例(不再俘获瞬时实例)+ Checksum 语义文档化(身份指纹,非内容指纹);限流 OnRejected 改为真实 ApiResult 序列化(与框架信封同源);补 5 项测试:宿主自动执行/迁移失败阻止启动/并发触发幂等/未知策略运行期报错/429 信封结构

## v0.0.5 (2026-10-25)

新增进程内事件总线 + 契约分发委托化 + 质量保障机制。行为兼容,公共 API 变化仅 `LoginRateLimiter` 构造函数新增可选 `TimeProvider` 参数;新增包 NexMesh.Events。

- **契约分发委托化**:`AutoApiDispatcher` 每请求三处反射调用(方法调用 / `Task<T>.Result` 取值 / 集合 `ToArray`)替换为启动期表达式编译的委托缓存(`ContractInvokerCache`);含 ref/out 参数回退。调用开销约为原实现 36%(见 docs/框架质量契约.md 性能基线)
- **CancellationToken 贯通**:契约响应序列化补充 `RequestAborted`
- **时间抽象**:`LoginRateLimiter` 使用 `TimeProvider`(默认 `System`,行为不变)
- **新包 NexMesh.Events(进程内事件总线)**:有界 Channel(容量与回压策略可配);启动期构建订阅注册表(编译委托,签名不合法启动失败);`[EventSubscribe]` 支持精确/枚举/正则匹配与细粒度重试(`NumRetries`/`RetryTimeoutMs`/`ExceptionTypes`);处理器经 `INexMeshBackgroundRunner` 执行;停机限时排干并上报剩余计数;OTel 指标 `nexmesh.events`(`published`/`retried`/`dropped`/`queue_depth`);`IEventMonitor`/`IEventFaultHandler` 扩展点。可靠性语义(异常隔离/取消语义/执行顺序/投递守恒等)见 docs/框架质量契约.md,共 26 项测试
- **质量保障机制**:tests/NexMesh.ArchTests(依赖规则测试 + PublicApiGenerator 公共 API 基线);benchmarks/NexMesh.Benchmarks(契约分发/登录限流/事件发布/上下文槽位四项基准)
- **文档**:新增 docs/包实战案例.md(各包分步骤接入案例)与 docs/框架质量契约.md(质量保障机制)

## v0.0.4 (2026-10-23)

Scalar 契约文档自生成器（自 v0.0.3 起 20 个提交）。

- **契约端点 OpenAPI 3.0.3 文档直出** `/openapi/v1.json`：反射式 RequestDelegate 端点不被 .NET 默认生成器收录，由 `ContractOpenApiDocument` 自产（仅依赖 Scalar.AspNetCore 做 UI）
- **字段级 schema**：属性递归展开 + camelCase + XML 中文注释 + 示例值；字典/KeyValuePair 序列收敛 map schema；信封响应 + 400/401/404/500 错误结构
- **IsSimple 单一源**：文档 schema 与参数绑定共用 `AutoApiDispatcher.IsSimple`（11 项边界断言防两套判定分叉）
- 侧栏"中文描述 METHOD /路由"合并展示；tags 短名冲突按接口计数；文档预序列化缓存 + 降级保护
- 移除 `Microsoft.AspNetCore.OpenApi` 依赖

## v0.0.3 (2026-10-23)

**约定对齐升级(约定级破坏变更)**——统一对外表面,单一约定:

- 信封 `ApiResult`：成功 `code=0`；失败 `success=false` + 业务码（前端 H5 依赖 `code=0`）
- 分页 `PagedResult<T>`（Items/TotalCount/PageIndex/PageSize/TotalPages/HasNext/HasPrevious）
- 异常族：`MeshException` + 11 个语义子类（Business/NotFound/Unauthorized/Forbidden/Validation/Duplicate/Remote/Unavailable/Timeout/…）
- 契约端点唯一约定：`[ApiContract]`/`IServiceApi` 接口 + 原生 `[Http*]`/`[Authorize]`，路由 `api/{服务名}/{模板}`；移除 `IAutoApi`/`[ServiceRoute]` 类模式
- 认证表面：不透明令牌（可吊销/滑动续期/Redis 会话）+ `ICurrentUser<TUser>` + `IJwtTokenService`/`ITokenStore`
- 数据表面：`AddDBSource<TDBSource>` 类型化多源 + `IRepository` 标记 + `IIdGenerator`
- 模块宿主 `AddNexMeshHost`/`UseNexMeshHost`;以大型业务系统全量迁移完成端点级验证(159 端点路由 100% 对账)

## v0.0.2 (2026-10-23)

修复 NuGet 发布认证（API key）。

## v0.0.1 (2026-10-22)

从零重建（Green-field），基于白皮书审核结论与双轮安全审查。

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

### 安全体系(双轮审查通过)

- 默认拒绝三件套（契约端点 / 网关 / 审批中心）
- 租户隔离三层闭环（全局过滤 / 写入填充 / 缓存键隔离）
- AI 安全门（High 工具启动阻断 / 启用后挂起拦截 + 终局语义 + 幂等键）
- 三层防御（断连熔断 / 上下文穿帮 / 工具自纠错）
- 登录限流 · 信息泄露防护 · CORS fail-closed · 文档生产门控

### 技术栈

.NET 10 · Microsoft.Extensions.AI 10.11 · FreeSql 3.5 · StackExchange.Redis 3.4 · Polly 8.8 · Cronos 0.13 · MiniExcel

### 测试

178 项全绿 · 零构建警告
