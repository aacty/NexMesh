# 更新日志

## v0.0.5 (2026-11-11,合并 0.0.6~0.0.8 全部内容)

> 本版对外版本号 0.0.5(NuGet 已发布 0.0.2~0.0.4),内容合并原 0.0.6(迁移+限流)、0.0.7(分布式锁/OIDC/gRPC)、0.0.8(Dashboard/自建 IdP/性能深挖/五轮交叉测试)全部交付。包数 13→12;测试 249→602 项。


- **三轮遗留清偿 + 幂等标准化 + 公共 API 全守护**。①**API 基线扩展到全部 12 包**:此前仅 4 包守护(8 包公共 API 变更无 diff 评审防线)——ArchTests 补 4 工程引用+12 InlineData,基线 1898 行全守护(Cache 基线随 IdempotentResponse 公共面再生)。②**幂等中间件升级为标准幂等**(L-F4 落地):完成态响应回放——24h 窗口内同 Key 重试拿到与首次一致的响应(X-Idempotent-Replay 头),不再重复执行("响应丢失后的网络重试不再重复扣款"从注释声明变为实测事实);5xx 不回放(服务端不确定态,重试拿新鲜尝试);4xx 回放;并发在途仍 409/40901,持锁完成与 409 之间补竞态复查(能回放则回放)。测试 6 项:内存模式回放/不同 Key 独立/4xx 回放/5xx 不回放 + 真 Redis 串行回放与并发-完成-回放全链路。③**顺带挖出 P1:纯内存模式 SetAsync 静默丢弃**——_redis=null 且未配 L1Ttl 时所有 Set 不落任何存储、TTL 参数被忽略(回放测试钓出;修复=内存模式 L1 即主存,TTL=调用方 ttl ?? 默认)。④P3 三项:工具名 Async 裁剪与 M.E.AI Factory 对齐(短名一致性);缓存内存模式 RemoveKeyAsync 诚实化(先查存在再删,不存在键=false,H 备案测试反转);**IMeshExceptionHandler 死扩展点接活**——全局异常中间件按 Priority 升序消费已注册处理器,IsTerminating=true 接管响应写出(此前全仓零消费,业务注册了也不会被调用)。⑤测试方法学教训:回放测试两处把 Idempotency-Key 加错对象(响应而非请求)——HTTP 头测试必须用显式请求构造器;TestServer 全链路探针(slotbench)+单元探针二分定位"中间件 vs 测试自身"。全量 13 工程 602 项 0 失败

- **三轮四架构师交叉测试(I 配置与模块宿主/J 工作流检查点/K Toolbox 数据往返/L 分布式缓存与 gRPC 边界)+ 修复 + 真浏览器 UI 业务测试**。①【I·7 项实证】**配置源优先级倒置**(Configs/*.json 落在环境变量之后反向覆盖,Docker/K8s 环境变量覆盖全部失效→Insert(0) 沉底)、嵌套占位符 ${A:${B}} 静默产垃圾值、**开放泛型实现类约定注入运行期必炸**(开放接口 Namespace=null 陷阱,改开放定义转发)、Microsoft.* 接口被约定误注册(captive dependency)、模块重复装配无防护(AddModule 去重+AddNexMeshHost fail-fast)、HttpPort=GrpcPort 启动期晦涩崩溃(装配期检测)、零 Security 装配必炸(兜底 AddAuthorization)。②【J·P0+2P1】**工作流状态机非法流转未拒绝**(Failed/Pending/Running/99 一律当成功推进直至假 Completed→门禁落盘 Failed);**RecoverStaleAsync 丢失更新**(预读快照整行写回可复活已完成流程→TryMarkSuspendedAsync CAS);取消被误标 Failed 终态(长流程随一次停机全灭→OCE 落 Suspended 可续跑);序列化异常吞原始异常;坏 DataJson/GetInput null 裸异常包 MeshException;附检查点/恢复语义图(设计保证 vs 未定义边界,16 项测试钉住)。③【K·安全+高】**LocalFileStorage 路径遍历**(id 含 ../ 越界读删任意 .bin→IsValidId 仅收本实现签发的 hex id)、**Masking.Name 毁掉嵌套文本**(任何 ≥2 字符串整段打星→2~6 汉字白名单)、Phone 负号串被打码、Email/Name 劈开 emoji 代理对、IdCard 无格式校验、FileStorage 往返丢元数据(.meta 伴生文件);Excel 数值精度/换行归一判定为上游固有(测试钉住崩溃行为+string 列正向指引)。④【L·真 Redis】**缓存锁续期不校验令牌**(陈旧锁对象给他人锁续命 30s→Lua 令牌匹配才 PEXPIRE)、**击穿防护慢回源放大为并发回源**(600ms 回源 32 并发→加载器执行 32 次→复查+续等循环,慢回源恰好 1 次)、gRPC 取消被包装成 OK+失败信封抢答(OCE→规范 RpcException CANCELLED);幂等"完成后重试重新执行"语义取证备案(标准幂等需响应缓存,未擅动);迁移锁真 Redis 全过(接管 131ms 级/错误令牌拒绝/无死锁)。⑤【三轮 UI 业务测试·真浏览器钓出 P0】**Dashboard 内嵌页面脚本语法死亡**——缓存/作业面板按钮的内联 onclick 引号错配(此前 478 项测试全绿:全是 HTTP 级,从未执行页面 JS),任何真实浏览器登录/面板/操作全部失效;修复=data 属性+闭包绑定(三处)+语法守卫测试(提取内嵌脚本 node --check,无 node 退化启发式);浏览器全流程复验:登录→7 面板渲染→作业立即执行(执行记录出现)→缓存巡视(内存模式诚实提示)→截图留证。⑥基线再生(Core/Auth/Host 签名变化)。全量 13 工程 590 项 0 失败(三轮净增 112 项)

- **四架构师二轮交叉测试(换面攻击:资源泄漏/输入模糊/AI 管道/运维可观测)+ 批量修复 + 实机业务流二轮**。①【P1 实证】绑定路径 4 处 500:Content-Type 谎称/缺失(InvalidOperationException 只捕 JsonException)、multipart 异常族逃逸、**HashSet/ISet 集合参数提供值即 InvalidCast**(识别器承认合法、执行器强转 IList 自相矛盾)——统一 40001 客户端错误;附带:标量重复键取首值(MVC 语义,修复 decimal "1,25" 静默变 125 的数据损坏)、集合混合形态统一拍平、空 multipart 缺字段 → 40001(此前 null 进业务 NRE)。②【P1 实证】**错误码契约系统性漂移**——MeshException.ErrorCode(40001/40401/40903/50004/50004)从不进信封(文档承诺对齐):信封 Code 改用 ErrorCode,HTTP 状态与信封 Code 分离(WriteErrorAsync 拆参);网关 40400/40500、幂等 40901、登录 50004 抛码点补齐;字面量模式 403 补 HandleForbiddenAsync 信封(此前空 body 违背"全信封"承诺);未知异常兜底 50000。H 的 15 项备案/漂移测试全部反转为文档对齐断言。③【高危实证·已修】语义缓存匿名跨租户泄漏(空 scope 跳过过滤,匿名命中租户 PHI 答案)→ 存储键 scope+"|" 命名空间硬隔离;【实证·已修】HashEmbedder ≥257 字符位置分桶碰撞(半块交换/分桶和相等→逐位相同向量→缓存错配)→ FNV 内容哈希折叠(诚实边界:同字符多重集文本仍高相似,频率嵌入器定位=开发用,生产配真嵌入模型)。④【P2 实证·已修】事件总线停机排干令牌被宿主停机污染(排干 dispatch 经 BackgroundRunner 联动 ApplicationStopping,联合令牌创建即取消)→ INexMeshBackgroundRunner 新增 RunDrainAsync(DIM 默认委托,零破坏;排干分发不联动停机令牌);限流策略名含冒号静默截断 → 注册期 fail-fast;AI Provider Type 拼写错静默落 openai-compatible → 启动 fail-fast;Dashboard 缓存未知源 500 → 结构化 400(消息含源名);ops 端点 HTTP 状态与信封 code 对齐(404/404 等 6 处)。⑤【已修·授权内】工具注册表同名静默覆盖 → 启动期 fail-fast(NotSupportedException,消息含两个类型全名;TrimAsyncSuffix 后同名算冲突;Reflection.Emit 探针不污染扫描);槽作用域 disposed 守卫+世代校验(双重 Dispose no-op/错序不覆盖后来者)。⑥守恒长跑:10 万事件×8 并发满载回压,峰值水位=capacity 内存有界、dropped=0、MeterListener 计数守恒;Worker 异常风暴/停机收场/重叠防护补齐。⑦实机业务流二轮(离线 stub):登录→CRUD→事件发布→**溢出参数信封 40001**/**未知缓存源 400 含源名**/**未知作业 HTTP 404**/**会话踢下线 revoked=1→被踢 401 他号 200**→**限流 3 次后 429**——全链路真实数据。P3 备案未修(工具名 Async 裁剪不一致/缓存内存模式 removed 恒 true/IMeshExceptionHandler 死扩展点);OpenAPI 简单集合 body 漂移 1 项 Skip 保留(下轮)。基线再生(Core +RunDrainAsync/Auth +HandleForbiddenAsync/Host ops 签名)。全量 13 工程 478 项 0 失败(二轮新增 126 项攻击/防护测试)

- **实际业务测试钓出真 Bug(P1)+ 全业务闭环实证(AiDemo × DeepSeek 真模型)**:①【P1·实证】**审批包装器丢失工具参数 Schema**——`ApprovalRequiredAIFunction`/`TelemetryAIFunction`/`InstanceToolAIFunction` 三层包装均未转发/构建 `JsonSchema`,基类默认=空参数对象,审批启用后**所有工具对真模型"无参"**(stub 时代不可见;实弹:DeepSeek 两次独立报告"工具无参数"、调用被框架拒绝)。修复 = 实例工具按 MethodInfo 构建参数 schema(类型映射 string/integer/boolean/number+required 判定,按 MethodInfo 缓存)+ 遥测与审批包装器全量转发 inner 元数据;AToolSchemaTests 2 项锚定(包装前后 schema 逐字节一致/参数类型正确)。②**真模型全业务流(真实 DeepSeek API)**:登录→业务写入(创建员工,合并后 Data.FreeSql)→真模型调 QueryOrder 成功取回业务结果(物流单号逐字回传)→真模型提交 ForceCloseOrder→审批门自动挂起(单号回传给模型,模型如实转告并阻止重复提交)→Dashboard 批准→status=Executed、业务结果真实执行→终态进 history(decidedBy=admin-001)。③演示改进:高危工具描述如实声明"调用即自动进入审批队列,不会直接执行"(模型合规提交的前提;模型对高危写的谨慎拒绝本身是正确对齐行为)。④壳层教训:Windows GBK 控制台 curl 中文 payload 会产生坏字节→400(ASCII 载荷复验);`dotnet run` 的 apphost 进程名是 NexMesh.AiDemo.exe(非 dotnet.exe),按映像名清理僵尸实例。352 项全绿

- **四架构师交叉测试 + 双 P0 修复(攻击测试先行实证,修复后测试反转入库)**:四个并行攻击者(并发状态机/安全/数据迁移/契约文档)+ 第五方裁决。①【P0·实证】**gRPC 面授权完全缺失**——REST 默认拒绝/[Authorize]/角色在 gRPC 被匿名直通(同契约 REST 401 vs gRPC 200 拿到业务明文,低权令牌经 gRPC 角色门照样失效);修复 = ContractGatewayService.Invoke 镜像 REST 虚拟网关同一判定(同一 AllowAnonymousByDefault 配置源),未认证→Unauthenticated/角色不足→PermissionDenied,B 的 6 项绕过测试反转为双面一致断言(+Admin 正确放行)。②【P0·实证】**审批幂等键 TOCTOU**——查重与入队无原子性,48 并发同参高危调用产生 20 张待审单、逐张批准=执行 20 次(原子认领防的是同单并发决策,防不住多单重复执行——修复打错了靶心的一半);修复 = ApprovalGate 64 条带锁(按幂等键哈希分条,有界内存无清扫),A 的战争测试反转为恰好一张单。③【P1·实证】UpdateAsync 无状态守卫(陈旧 Pending 快照可复活已认领/终态单→二次认领)→ 双 store 状态机单调守卫(禁向后写回+终态不可变,FreeSql WHERE 排除/InMemory 读取守卫,409);A 的写回测试反转。④附带发现:工具注册表同名静默覆盖(P3 备案)、探针工具 Contains 匹配劫持(AIdemProbe 含 "Probe")——测试互踩教训入库。⑤文档审计 F1-F7 全修:README badge/技术栈 211→350、源码目录说明整文件(11 包 178 项→12 包 307+真实测试清单/5 benchmarks/9 docs)、白皮书依赖图补 Events 与 Host 实际 7 依赖、README Host 传递闭包表述、能力表 23 补七面板、模板版本 0.0.1→0.0.4;C 的两条:Runner 头注释自相矛盾纠偏(分布式锁已交付)、配置参考+包实战案例补"无 Redis 单实例语义"与"IdP user_id 直传安全边界"声明。ArchTests 10/10;基线再生(ContractGatewayService +IConfiguration 参数)。全量 13 工程 350 项 0 失败

- **REST 绑定探测缓存(热路径收官后追加,实测抓到最大单点)**:每参数每请求的类型探测反射收编为元数据缓存——①`GetCustomAttribute<FromForm>` **每个复杂(DTO)参数每请求 1595ns/528B**(特性每次重新实例化,比上一轮修的 TypeDescriptor 转换贵 40 倍)→ ContractInvokerCache.HasFromForm 按 ParameterInfo 缓存;②IsSimpleCollection 对 `List<int>` 每次做 MakeGenericType(**358ns/96B**)→ ProbeSimpleCollection 按 Type 缓存;③启动期 BodyParam 判定(ContractApiExtensions)同走缓存(同一结论源)。缓存命中为字典查找(纳秒级/零分配)——每个带 DTO body 的请求省约 1.6μs/528B。**过程中实战纠偏一处真正的回归**:HasFromForm 初版误探测 Mvc 同名特性——框架自有 NexMesh.FromFormAttribute(namespace 就近规则,AutoApiDispatcher 历史短名解析即此特性),探测错对象导致 FromForm 参数掉进 JSON 分支返回 null→500,既有 AdvancedBindingTests 当场抓获,独立复现程序闭环修复;命名空间就近遮蔽与 proto 教训同族,**跨文件搬探测逻辑必须核对短名解析目标**。307 项全绿

- **SSS+ 审核收口(绑定快路径批次:等值性测试+基准入库+AiDemo 实机)**:①SimpleConverter 等值性 5 项测试锁定(快类型语义/char 与枚举回退不变/溢出与格式错误同为 40001/千位分隔符语义与 TypeDescriptor 一致——"1,25"→125 属 NumberStyles.Number 的 AllowThousands,与旧 DecimalConverter 逐位一致,审核者本人的"应报错"假设被测试当场纠正);②基准入库 benchmarks(NexMesh.Benchmarks 新增 BindConvert 两组,Core 开 InternalsVisibleTo 给 Benchmarks 走真实生产路径);③**AiDemo 实机军演**:合并后迁移建库+种子数据正常、登录→契约绑定(id/分页缺省 DefaultOf)正常、**溢出参数实机 400(修复前 500)**、格式错误 400、dashboard 七面板探测正确(migrations=False 系 AiDemo 未装配迁移的正确探测)、审批中心 history 字段就位。307 项全绿

- **REST 契约绑定快路径(每请求热路径,真实 A/B)**:ConvertSimple/ConvertElement/表单绑定的简单类型转换从 TypeDescriptor.GetConverter().ConvertFromInvariantString 改为 SimpleConverter 直接 Parse(枚举等冷类型回退 TypeDescriptor,行为不变);缺省值兜底 Activator 装箱改按类型缓存(值类型调用点 unbox 即拷贝,共享安全)。**入库基准(BDN 同口径 Release,基准入 benchmarks 工程)**:int 38.0→8.9ns(−77%)/Guid 55.5→17.7ns(−68%);分配持平(装箱结果主导,转换器内部分配本为零)——修正此前快速手测的"24B→0B"口径错误(那是装箱版对比不装箱版)。**端到端严格 A/B(stash 复测防噪声)**:带 3 简单参数契约端点 p50 17.1→16.7μs(~2–3%)——诚实结论:转换开销在完整 HTTP 请求中占比小,本轮价值=每参数转换成本砍约七成,**非**端到端大涨幅(首个"改前"基线 22.2μs 系冷态噪声,严格复测后收益修正)。**行为修复**:数值超范围(如 int 传 99999999999999999999)此前 OverflowException 未捕获→500,现统一 40001/400(客户端输入错误);新增锚定测试

- **SSS+ 深审(OIDC IdP 安全加固 + gRPC 恶意输入面,新增 6 项测试)**:①**client_secret 比较改常量时间**(CryptographicOperations.FixedTimeEquals,防计时侧信道逐字节爆破;null 密钥视为未配置一律拒绝)——RP 侧 HMAC 验签早已用 FixedTimeEquals,IdP 侧漏齐;②**IdP 时钟可注入**(OidcIdpTokenService 可选 TimeProvider,沿用 LoginRateLimiter 先例)——授权码 90 秒过期从不可测变为可拨钟测试;③gRPC 网关非法 args_json 从"服务内部错误"(Error 告警)分离为"参数 JSON 非法"(Warning+400)——客户端输入错误不再污染服务端告警信噪比。新测试:授权码过期拨钟拒绝/并发兑换同一码恰好一人成功(TryRemove 原子)/并发轮换密钥 JWKS 与签名一致/未注册 redirect_uri 与未知客户端精确拒绝(open-redirect 防线)/gRPC 恶意 ArgsJson 独立信封。排查插曲:FreeSql.Tests 全量 SQLITE_CANTOPEN 20 项失败——独立最小复现通过+内存/磁盘/进程逐一排除,**bin 产物污染,清重建 30/30 全绿**(连续跨工程增量构建的陈旧 System.Data.SQLite 产物);Auth API 基线再生(TokenService 构造加 TimeProvider 可选参数)。301 项全绿

- **SSS+ 深审(审批决策并发战争测试):修复高危工具双执行竞态——原子认领**。战争测试先行实弹坐实(check-then-act:读 Pending→无条件覆盖写之间无原子性):16 并发批准同一待审单,真库(FreeSql/SQLite)**执行 7 次**、内存实现**执行 12 次**——审批门要防的事(高危工具自主/重复执行)恰在其自身决策路径上发生。修复:`IApprovalStore` 新增 `ClaimForDecisionAsync` 默认接口方法(原子认领 Pending→目标状态并盖章,返回认领后的单;null=判负)——InMemory 用 ConcurrentDictionary 引用 CAS(64 次重试上限,持续争持 fail-closed 判负)、FreeSql 用单语句 `UPDATE...WHERE status=pending` 看影响行数、默认实现为非原子回退(自定义实现者零破坏,文档指引覆写为存储级原子);决策服务 Approve/Reject 全部改走先认领后执行,**并发恰好一人胜出、败者 409、工具执行至多一次**。战争测试 3 项入库(真库并发批准/真库批准拒绝混合/内存 CAS);顺序 409 语义不变(既有测试全绿)。附带发现:**OidcIdpTokenService.RotateKey 零测试覆盖**——补轮换闭环测试(轮换→/jwks 自动吐新 kid→新令牌验签通过/旧令牌对新密钥集失败)

- **SSS+ 审核(审批历史+上下文绑定+包合并三批,实弹修复 1 处契约违约)**:①**FreeSql Take(≤0) 忽略 LIMIT**——临时工程对真 SQLite 实测 `Take(0)`/`Take(-1)` 均返回全表(不生成 LIMIT 子句),FreeSqlApprovalStore.ListHistoryAsync(0) 曾返回全部历史违反"取前 limit 条"契约;修复为 ≤0 显式守卫空表,契约文档同步,新增 Theory(0/-1) 内存+真库双断言(InMemory LINQ Take(≤0) 天然空表,两实现行为对齐)。②上下文绑定补两条缺失覆盖:自定义 accessor 组合回退路径(RecordingAccessor:Enter 一次/身份租户照常绑定/任务后还原)、无快照仅清租户(遗留租户在任务内 fail-closed 不可见/身份槽不伪造/任务后外部租户原样还原)。③走查通过项:发布流水线 `dotnet pack NexMesh.slnx` 全量打包无硬编码包列表(12 包结构自动适配)、TenantAccessor 委托 Core TenantHelper 单一 AsyncLocal 实例(槽位合并无漂移)、中间件声明投影 null Type 与旧 LINQ 行为对齐、HistoryTests 临时库清理不掩盖结果。292 项全绿

- **数据库迁移并入 Data.FreeSql(13→12 包,用户拍板)**:迁移源文件(迁移链 Runner/Redis 分布式锁/模型/注册扩展)git mv 进 Data.FreeSql 包,**命名空间 `NexMesh.Data.Migrations` 原样保留**(沿用 ServiceDiscovery→Cache 先例:保留原命名空间零用户代码破坏,类型只是搬进同一程序集);FreeSql 包 += StackExchange.Redis/AspNetCore FrameworkReference;旧 Migrations 包与测试工程删除,MigrationRunnerTests(18 项)并入 FreeSql.Tests;ArchTests 规则 6 改指向 FreeSql 程序集(数据层特性包不得横向依赖 AI/Auth/Cache/Events/Host),API 基线 NexMesh.Data.Migrations.txt → NexMesh.Data.FreeSql.txt(222 行);slnx 34 工程与磁盘完全对齐;文档全面对齐(README 能力表/12 个包×3 处/白皮书头部/包实战案例 §13/大型项目指南)。用户代码零改动:配置节 NexMesh:Migrations、AddNexMeshMigrations、NexMeshMigration 全部原名原空间

- **审批终态历史查询 + 包并入评估(路线图评估项收口)**:①IApprovalStore 新增 `ListHistoryAsync` 默认接口方法(Executed/Failed/Rejected 按决定时间倒序取前 N;未覆写的自定义实现返回空表零破坏,同 ISessionStore.ListActiveAsync 先例);InMemory/FreeSql 双实现(FreeSql COALESCE 排序);Dashboard 审批中心端点/面板/前端"终态历史"表(状态枚举投影字符串)。测试 4 项:InMemory 排序与上限/默认接口方法空表/SQLite 真库查询/Dashboard 端到端(批准后倒序居首、decidedBy 随令牌)。DIM 语义实证两处:接口默认参数值不流向实现签名(实现须自带 =20 默认)/默认方法体只能经接口类型调用。②**包并入评估结论**:SemanticCache 不并入 AI(并入需 AI 引 Cache→Redis,违反 NexMesh.AI 零 DB/Redis 架构铁律,维持特性包形态与 Approval 同模式);Data.Migrations 技术上可并入 Data.FreeSql(迁移记录/UpAsync 全跑在 IFreeSql 上,同库同语义,符合 ServiceDiscovery→Cache 先例)但涉及命名空间/基线/模板/文档大动,**待用户拍板**;AI.Review 维持搁置(用户明示先不做)

- **上下文槽路径分配深挖(挂账项闭环,真实分解实测)**:逐层实测定位——单次 Enter+Dispose 96B(作用域对象 24B + AsyncLocal/ExecutionContext 机制 ~72B,后者为**设计地板不可消除**),读 Current 0B;**"232B 闭包来源"嫌疑排除——真凶是中间件声明投影的 LINQ 链**(Where+GroupBy+ToDictionary 1336B/请求:多级枚举器+Lookup 分组对象)。修复两处:①声明投影改 foreach+TryAdd(同语义:每声明类型首个非空值胜出,576B/请求,-57%);②请求入口/BackgroundRunner 双槽绑定收口为单作用域(ContextSlotBinding:默认 accessor 单对象双槽还原,自定义 accessor 自动回退组合两次 Enter,无快照时身份槽不动仅清租户——原语义逐条保持,框架内部实现零 API 面变化)。端到端实测:UseNexMeshContext 管道 14,270→13,616 B/请求(中间件自身路径 -40%)。全部回归 14 工程 284 测试 0 失败

- **gRPC 网关热路径零反射(真实基准对比)**:三处每请求反射残留清除——①Registry 索引一次性构建(此前每个新 (service,method) 首调触发全量契约重扫,首 M 次调用 O(M²));②每端点调用计划(GatewayPlan:参数元数据快照+编译调用器+编译 Task&lt;T&gt;.Result 读值器)按需缓存,替代每请求 GetParameters()/GetProperty("Result").GetValue;③REST 调度器同款 GetParameters 每请求分配一并收编(ContractInvokerCache.GetParameters)。**实测**(benchmarks/NexMesh.GrpcLoadTests 新增 latency 模式,单通道顺序 20 万次可复跑):mean 36.65→31.3μs(−15%)/p95 136.7→101μs(−26%)/max 14.8ms→5.5ms(−60%+);p50 持平 ~16μs——稳态地板是 gRPC 帧+JSON 本身,本轮收割的是尾部与均值。10M 吞吐军演 598,939 req/s(同机上一批 465,159,+29%),ok=10M err=0 载荷全对。公共 API 基线更新(Find 返回 GatewayPlan+两个计划类型)。**SSS+ 审核两处实弹修复**:索引发布字段补 volatile(弱内存模型下锁外读者可见完整构建字典)+ lock(this) 改私有锁对象(公共类锁自身可被外部劫持);新增 48 线程并发首触竞态专项(混合多方法各首次/同方法齐首次/大小写不敏感路径,全对)

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
