# NexMesh.AI v2.0 架构白皮书

> Green-field 重构版。基于 `docs/审核-NexMesh.AI白皮书-v1.0.0.md` 的修正结论演进。
> 技术栈：.NET 10 Minimal API + FastEndpoints（业务层可选）+ FreeSql + Redis (Redis Stack) + Microsoft.Extensions.AI + Agent Framework。

## 一、四层架构设计

1. **业务层**：FastEndpoints (REPR) + FreeSql 仓储 + `[NexMeshTool]` 业务桥梁。
2. **编排治理层**：审批门（Approval Gate）、长流程 HITL 状态机、ToolSearch 召回。
3. **AI 核心服务层**：NexMeshChatFactory、管道装饰器、OpenTelemetry gen_ai 遥测。
4. **基础设施底座**：.NET 10 Minimal API、Microsoft.Extensions.AI、Agent Framework。

**核心铁律：NexMesh.AI 核心层与 FreeSql/Redis 零耦合。** FreeSql 仅服务业务层——业务服务注入 `IFreeSql` 完成数据操作，再通过 `[NexMeshTool]` 暴露给 AI。

## 二、FreeSql 与 Redis 分工矩阵

| 存储组件 | 承担职责 | 适用场景 |
| :--- | :--- | :--- |
| **FreeSql** | 关系型核心业务数据 | 订单、用户、权限、高并发 CRUD、业务事务、对一致性要求极高的核心数据、审计、软删除、分库分表 |
| **Redis (Redis Stack)** | 向量检索 / 语义缓存 / 状态同步 | 知识库 RAG 向量检索、对话历史 Session 缓存、语义去重、高频限流、Agent 状态暂存 |

## 三、业务层标准写法（FreeSql + [NexMeshTool]）

```csharp
namespace NexMesh.AiDemo.Services;

using FreeSql;
using NexMesh.AI.Tools;

public class OrderManagementService
{
    private readonly IFreeSql _orm;

    // 直接注入 FreeSql 实例（或使用 IBaseRepository<T>）
    public OrderManagementService(IFreeSql orm)
    {
        _orm = orm;
    }

    [NexMeshTool("根据订单号查询订单实时详情。低风险操作。", NexMeshRiskLevel.Low)]
    public async Task<OrderEntity?> GetOrderDetailsAsync(string orderId)
    {
        // FreeSql 优雅的 Lambda 查询语法
        return await _orm.Select<OrderEntity>()
            .Where(o => o.Id == orderId)
            .FirstAsync();
    }

    [NexMeshTool("对指定订单执行管理员强行关闭。高危写操作！", NexMeshRiskLevel.High)]
    public async Task<string> ForceCloseOrderAsync(string orderId, string reason)
    {
        var affected = await _orm.Update<OrderEntity>()
            .Set(o => o.Status, "Closed")
            .Set(o => o.CloseReason, reason)
            .Where(o => o.Id == orderId)
            .ExecuteAffrowsAsync();

        return affected > 0
            ? $"订单 {orderId} 已成功强制关闭。"
            : $"未找到订单 {orderId} 或关闭失败。";
    }
}
```

> ⚠️ **Phase 1 安全红线**：High 风险工具在 Phase 1 **不可被 AI 调用**——工具注册器发现即抛 `[NexMesh Security Gate]` 异常阻断启动，审批门（Approval Gate）于 Phase 2 交付。

## 四、业务极简（一行宿主总装 + 约定注入 + 双表面 + 完整 CRUD）

**开发者零心智清单**（框架铁律：以下内容业务开发者永远不写）：

| 不写 | 由谁处理 |
| :--- | :--- |
| DI 注册（AddScoped/AddTransient…） | 标记接口自动扫描注册 |
| 生命周期心智 | `IScopedService` 一个标签即完事 |
| 配置装载（json/环境变量/占位符/热更） | `Configs/` 目录约定 + `${ENV\|默认}` |
| 数据库接线（连接串/Provider/建表） | `NexMesh:Db` 配置节，缺省 SQLite 零基础设施 |
| AI Provider 管道（Key/模型/重试装配） | `NexMesh:AI` 配置节，工厂预构建 |
| 中间件顺序 / 路由注册 / 健康检查 / 文档 | 宿主总装 |

整个微服务（集群中 20 个之一）的 Program.cs：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddNexMeshService();   // 全家桶总装:配置/约定注入/FreeSql/AI 底座
var app = builder.Build();
app.UseNexMeshService();       // 管道/AutoApi/健康/Scalar
app.Run();
```

业务开发者产出的全部代码 = 业务服务类（标记 `IScopedService` + `IAutoApi`，注入 `IFreeSql` 写逻辑）+ AI 工具方法（`[NexMeshTool]`）。几千人规模的员工 CRUD 完整契约：

```csharp
public interface IEmployeeBiz : IAutoApi
{
    Task<long> CreateAsync(EmployeeDto dto);                       // POST   /api/employee/createasync   (Body)
    [HttpGet("{id}")] Task<EmployeeDto?> GetAsync(long id);         // GET    /api/employee/{id}         (路由参数)
    [HttpPut("{id}")] Task<bool> UpdateAsync(long id, EmployeeDto dto); // PUT /api/employee/{id}     (路由+Body 混合)
    [HttpDelete("{id}")] Task<bool> DeleteAsync(long id);           // DELETE /api/employee/{id}        (软删除)
    Task<PagedData<EmployeeDto>> SearchAsync(string? keyWords, string? department, int pageIndex = 1, int pageSize = 20);
    Task<long> GetCountAsync();
}

[ServiceRoute("api/employee")]
public class EmployeeService(IFreeSql db) : IEmployeeBiz, IScopedService
{
    // 直接注入 IFreeSql,分页检索 + 软删过滤 + 审计时间,业务逻辑一目了然
}
```

**解耦三铁律**：业务切片仅依赖 FreeSql + 标记特性（不感知框架内部）；Core 与 AI 两个框架包互不依赖；NexMesh.AI 核心零数据栈引用（有架构约束测试固化）。同一个服务类在 P2 挂上 `[NexMeshTool]` 即成为 AI 工具表面——业务只写一份。

## 五、Redis 向量与语义缓存（Phase 3 交付）

### 5.1 Redis OM .NET 向量实体

Redis 官方 Redis OM .NET 原生支持实体对象化、向量字段定义与 LINQ 风格查询：

```csharp
[Document(StorageType = StorageType.Json, IndexName = "product-idx")]
public class ProductVectorEntity
{
    [RedisIdField]
    [Indexed]
    public string Id { get; set; }

    [Indexed(Algorithm = IndexAlgorithm.HNSW, Metric = VectorMetric.Cosine, Dimension = 1536)]
    public float[] Embedding { get; set; } // 向量字段

    [Indexed(Sortable = true)]
    public string Category { get; set; }
}
```

### 5.2 语义缓存（Semantic Cache）

大规模 AI 应用中 Redis 最强悍的落地场景之一：用户提问时先计算 Embedding，在 Redis 中做向量相似检索；若历史上有人问过语义高度相似的问题（如"怎么修改密码"与"重置密码的方法是什么"），直接从 Redis 秒级返回上一次大模型的回答。

**效果：大模型调用成本直接降低 50% 以上，响应延迟从几秒缩短至几十毫秒。** 实现形态：ChatClient 管道装饰器（IChatClient 装饰链），Phase 3 交付。

## 六、SPD 功能对照矩阵

新框架必须全覆盖 `InCloud.SPD.API` 的功能面（语义全保留、实现现代化替换）。核心闭环：

| 原 SPD.API 功能 | 新 NexMesh 现代化实现方案 | 所在包/模块 | Phase 落位 |
| :--- | :--- | :--- | :--- |
| Autofac 约定扫描 | .NET 10 原生 DI 标记接口（`IScopedService` 等） | `NexMesh.Core` | Phase 1 |
| ServiceBundle 动态代理 | 全新 AutoApi（`[ServiceRoute]` + 特性路由映射） | `NexMesh.Core` | Phase 1 |
| NLog 日志组件 | Serilog + OpenTelemetry gen_ai 统一标准 | `NexMesh.Core` | Phase 1 |
| Hangfire (MySql) | 轻量 Cron Worker（兼容后续扩展） | `NexMesh.Core` | Phase 3 |
| 动态配置（多 json） | `AddNexMeshConfig`（`${ENV\|默认}` 占位符展开） | `NexMesh.Core` | Phase 1 |

盘点补全（SPD 全量能力 → 落位）：

| 原 SPD.API 能力 | 新实现方案 | 所在包/模块 | Phase |
| :--- | :--- | :--- | :--- |
| 统一返回 ApiResult/PagedData | ApiResult/ApiResult\<T\> + PagedData + 分页请求 | `NexMesh.Core` | Phase 1 |
| 自研 ServiceHost 宿主 | 标准 WebApplication（Minimal API） | `NexMesh.Core` | Phase 1 |
| 不透明 Token 认证（滑动续期/Memory\|Redis 缓存） | NexMesh.Auth 登录路由 + 滑动续期 + 双缓存 | `NexMesh.Auth` | Phase 2 |
| ICurrentUser/SessionContext | ICurrentUser + HttpContext 会话上下文 | `NexMesh.Core` | Phase 1 |
| FreeSql 多库（BaseDB/HisBaseDB）+ CodeFirst | 命名多库注册 + CodeFirst Sync + 审计自动填充 + 软删 + 雪花 ID | `NexMesh.Data.FreeSql` | Phase 2 |
| BaseRepository 仓储 + ICoreRepository 扫描 | IRepository\<T\> + 约定注入 | `NexMesh.Data.FreeSql` | Phase 2 |
| 双层缓存（CloudEngine.Caching + FreeRedis） | L1(Memory) + L2(Redis) 统一治理 | `NexMesh.Cache` | Phase 3 |
| ExceptionFilter（仅 Dev） | 全环境异常中间件（业务码透传） | `NexMesh.Core` | Phase 1 |
| Swagger 配置化 + CORS 配置化 | Scalar + 配置化 CORS | `NexMesh.Core` | Phase 1 |
| 文件上传（DB blob + 无限制请求体） | 文件 Blob 存储 + 请求体限制配置 | `NexMesh.Toolbox` | Phase 4 |
| Mapster 映射 | Mapster 全局注册（Flexible 名称匹配） | `NexMesh.Mapping` | Phase 2 |
| 多院区上下文（HOS_CODE/BRANCH_CODE） | 多组织/多院区上下文泛化 | `NexMesh.Core` | Phase 3 |
| Excel/邮件/文件/脱敏 | Toolbox(MiniExcel+SMTP+IFileStorage[Db/Local/OSS 接缝]+Masking) | `NexMesh.Toolbox` | 已交付 9.8 |
| WCF/SOAP/签名 HTTP 第三方客户端 | HTTP 弹性客户端基座 + 签名助手 | `NexMesh.Toolbox` | Phase 4 |
| 服务心跳健康检查 | ASP.NET Core HealthChecks | `NexMesh.Core` | Phase 1 |
| DotNetty RPC / ApiGateway / 文件注册中心 / Consul / RabbitMQ / Skywalking / Azure / MediatR | **裁剪不搬**（未启用或已有标准替代：OTel 替代 Skywalking） | — | — |

## 七、Phase 1 能力边界（明确声明）

| 能力 | Phase 1 边界 | 后续演进 |
| :--- | :--- | :--- |
| AutoApi | **完整参数绑定**：[Http*] 动词与路由模板；简单类型从路由值/Query；数组/集合（逗号或重复键 Query）；单 JSON Body；`IFormFile`/`IFormFileCollection` 直绑；`[FromForm]` 表单对象；multipart 下 Body 从同名表单字段 JSON 解析（对象+文件混搭）；多 Body 仍映射期 fail-fast | — |
| [NexMeshTool] | **静态与实例方法均为一等公民**：实例方法每次调用从独立 DI 作用域解析实例（`IFreeSql`/`ICurrentUser`/`INexMeshChatFactory` 构造注入，生命周期框架托管；HttpContext 经 AsyncLocal 流动，请求上下文对新作用域可见） | — |
| High 风险工具 | 审批门未启用：发现即抛 `[NexMesh Security Gate]` **阻断启动**；启用后挂起拦截（见 9.3） | — |
| Tool Search | 不实现动态召回；`MaxToolsPerRequest`（默认 10）截断兜底防 Prompt 膨胀 | Phase 3 本地 Embedding 向量/关键字混合召回（Foundry BM25 为可选） |
| Chat 工厂 | Singleton 预构建字典缓存；openai-compatible 一套覆盖 OpenAI/DeepSeek/Ollama + `stub` 类型（离线开发） | Azure 分支 Phase 2 |
| 遥测 | gen_ai 调用期埋点（tool.name / risk_level / usage tokens） | Phase 5 全链路看板 |

## 八、上下文显式绑定（Context Propagation）—— 跨线程/离线任务断链处理

后台 AI 任务（Cron 定时触发、长流程挂起数天后经队列唤醒恢复）没有原始 HTTP 请求，HttpContext 为 null——身份、租户（院区）、TraceId、取消语义、Scoped 生命周期五条链同时断裂。**租户断裂 = 跨租户数据泄漏，是安全事故不是功能缺陷。**

处理总纲：**跨异步边界不传"活对象"，只传"不可变快照"；执行时用快照重建环境，在全新作用域解析依赖。**

| 断点 | 机制（已实现） |
| :--- | :--- |
| 身份/租户 | `NexMeshContext`（不可变快照：UserId/TenantId/Claims/TraceContext）；触发时经 `ToMessageHeaders()` 序列化进工作流状态/队列消息头/审批记录；恢复时 `accessor.Enter(snapshot)` 显式还原 AsyncLocal，Dispose 自动还原（嵌套安全） |
| 双源 ICurrentUser | 环境槽（显式绑定）优先、HttpContext 兜底——前端实时与后端离线同一套读取代码 |
| TraceId | 快照携带 W3C `ActivityContext`，`NexMeshBackgroundRunner` 以 Parent/Link 接续开新 Span |
| CancellationToken | Runner 令牌 = 调用方令牌 + 宿主停机令牌，与任何 HTTP 请求无关（严禁把 RequestAborted 带入离线任务） |
| Scoped 生命周期 | Runner 每任务独立 `CreateScope()`，从根容器解析依赖；禁止闭包捕获请求作用域服务（实例工具 `InstanceToolAIFunction` 同一姿势） |

```csharp
// 触发时(请求内):捕获快照随任务入队
var snapshot = provider.GetRequiredService<INexMeshContextAccessor>().Current;

// 执行时(离线,任意线程/进程):
await background.RunAsync(snapshot, async (sp, ct) =>
{
    var user = sp.GetRequiredService<ICurrentUser>();   // 读到原身份与院区
    var scorer = sp.GetRequiredService<INursingAiScorer>(); // 全新作用域
    await scorer.ScoreAsync(orderId, ct);                // ct 与请求无关
});
```

审批门（P2）复用同一机制：审批跨越数小时后恢复执行 = 从审批记录取出快照再水化，身份与租户不变。长任务断点续跑在此基础上叠加**作业检查点**（JobCheckpoint 落库，重启续跑）——快照保身份，检查点保进度，两者正交（P4）。

### 并发安全三铁律（写入代码评审门禁）

1. **环境槽退出必须"还原先前值"，严禁置空**：AsyncLocal 随 ExecutionContext 流动而非随线程驻留，线程池复用不会跨任务污染；置空反而破坏嵌套绑定（内层任务结束后外层快照被抹掉）——`Enter()` 返回的作用域 Dispose 时还原 previous 才是正解。
2. **严禁把 `HttpContext.RequestAborted` 传入后台任务**：后台令牌 = 调用方令牌 + `IHostApplicationLifetime.ApplicationStopping`（Runner 已内置链接）；P3 的 Cron Worker 基类沿用同一规范，且空转必须退避等待，不得忙轮询。
3. **严禁闭包捕获请求作用域的 Scoped 服务**：离线代码只从 Runner 注入的 `IServiceProvider`（全新作用域）解析依赖。

### 链路传播细节

- 快照携带完整 `ActivityContext`（TraceId + SpanId + Flags），经标准 W3C `traceparent` 头无损往返；**严禁用 TraceId + ParentSpanId 手工拼接** traceparent——ParentSpanId 缺位时全零 SpanId 非法，`TryParse` 静默失败导致链路断掉。
- 后台 Span 使用 `ActivityKind.Consumer`（队列消费者语义）；`ActivitySource` 必须静态单例，严禁每次调用 new。

### 三层隐藏灾难防御（已实现，测试锁定）

| 灾难 | 防御机制 |
| :--- | :--- |
| **大模型吸血鬼效应**（用户关浏览器后模型继续烧钱） | `NexMeshCancellationGuardChatClient` 挂工厂管道最外层：出站 LLM 调用令牌无条件链接 `RequestAborted`，断开即熔断流式/函数调用/计费；请求外自动退化为透传（离线由 Runner 停机令牌接管） |
| **AsyncLocal 上下文穿帮**（张三的请求读到李四的院区 → 病历越权） | 不可变快照 + 显式 Enter/还原先前值 + 双源 ICurrentUser；并行隔离测试锁定（20 并发绑定 × 5 轮线程池调度互不可见）；三铁律禁止单例缓存上下文、闭包捕获 Scoped |
| **模型输出与强类型契约错配**（日期写错/漏必填/枚举大小写 → Agent 循环崩溃） | 工具分发层自纠错：反序列化/执行异常转为 `[NexMesh Tool Error]` 结构化错误文本回喂模型，指明格式要求（yyyy-MM-dd、必填、枚举一致）令其反思重试，禁止编造结果；取消类异常仍上抛（熔断语义优先） |

## 九、AI 弹性管道与虚拟分发网关（已实现）

### 9.1 AI 弹性管道（Polly v8，`NexMesh:AI:Resilience` 配置节）

挂在工厂管道**最内层**——只包裹裸模型调用，重试/超时/熔断不波及业务工具与多轮 Agent 循环（30 秒超时不应击杀长工具）。管道顺序（外→内）：重试（指数退避，仅瞬时错误：网络异常/`ClientResultException` 429·5xx/超时）→ 熔断（失败率 50%、30s 采样、最少 8 次、开路 15s）→ 超时（每次尝试独立 30s，乐观协作式取消）。

**流式语义（关键设计）**：流式枚举是惰性的——直接把迭代器赋值给变量再塞进 Polly 是空操作（常见致命 bug）。正确实现经 Channel 生产者让枚举真正发生在管道内，且**重试仅覆盖"连接建立到首 token 下发"**——首 token 已写给调用方后重试会导致内容重复，此时异常包裹为不可处理类型直通、不参与熔断计数。默认值均可配置（`Enabled=false` 整体关闭，`MaxRetryAttempts=0` 跳过重试策略）。

### 9.2 虚拟分发网关（10M+ 逻辑路由，`MapAutoApiGateway`）

物理层面只注册一条通配路由 `/{*path}`（路由优先级天然低于一切字面量端点，与 `MapAutoApis` 可共存）；全部逻辑路由托管于内存 `LogicalRouteTable`——**两层结构**：精确字典（无参数路由，O(1)，10M+ 场景的绝大多数）+ 参数化模板列表（`{id}` 等模板段匹配，段数约束扫描）。ASP.NET Core 路由树（DFA）大小与逻辑路由数完全解耦。执行路径与字面量模式共用 `AutoApiDispatcher`（有界绑定/异常还原/结果解包行为完全一致），404/405 以 ApiResult 业务码区分。后续可平滑升级 Radix/FrozenDictionary，接口不变。

### 9.3 审批门（Approval Gate，已实现，`NexMesh.AI.Approval` 包）

High 风险工具从"发现即阻断"升级为**挂起拦截**（`NexMesh:AI:Approval:Enabled=true` 开启，未启用时维持启动阻断铁律）：

- **挂起语义**：High 工具调用经 `ApprovalRequiredAIFunction` 包装——不直达业务，幂等入队（ToolName+参数 SHA256 幂等键，模型重复请求合并同一审批单）后立即返回终局文本（含审批单号，明令模型停止重试、告知用户、严禁编造结果），阻断大模型对高危操作的自主闭环；
- **快照随队落地**：触发时刻的上下文消息头（身份/租户/链路）持久化进审批单——审批跨越数小时，批准后按快照再水化执行，**身份与租户不变**（有专项测试）；
- **批准执行**：`ApprovalDecisionService` 经 `NexMeshBackgroundRunner` 五步纪律（Consumer Span 接续/Enter 绑定/独立作用域/双令牌/全还原）执行注册器暴露的**原始函数**（`FindRawFunction`，绕过挂起包装防无限再入队）；执行失败落 `Failed` 状态不回喂任何模型；重复决策 409；
- **持久化**：`Store=Memory`（开发/测试）| `FreeSql`（生产，表 `nexmesh_approvals` 可审计可追溯）；
- **审批中心**：`MapNexMeshApprovals()`——GET `/api/approvals/pending`、GET `/{id}`、POST `/{id}/approve`、POST `/{id}/reject`（生产叠加授权策略，P2 Auth 交付后收紧）。

### 9.4 认证（NexMesh.Auth，已实现）

SPD 口径的**不透明 Token** 体系（服务端会话状态，可吊销可续期——这是相对 JWT 的安全优势）：

- **登录链路**：业务实现 `ILoginValidator`（用户名密码/扫码/短信统一扩展点）→ `POST /api/auth/login` 签发 256 位随机不透明 Token（40102 密码错误业务码）；`POST /api/auth/logout` 吊销；`GET /api/auth/me` 回显身份；
- **标准认证管线**：`NexMeshTokenAuthHandler`（默认 Scheme "NexMeshToken"）Bearer 解析 → 会话查找 → **滑动续期**（临期窗口自动延长，不突破最大总寿命）→ ClaimsPrincipal——身份/租户/自定义声明自然进入上下文快照与 ICurrentUser 双源（审批门批准执行读到的是**触发者**身份，闭环成立）；
- **无效凭据拒绝闸**：携带 Bearer 但无效/过期/吊销 → 一律 401（40101），绝不静默降级匿名；
- **AutoApi 授权标注**：业务类标注 `[Authorize]`（沿 InterfaceMap 上溯，`[AllowAnonymous]` 优先）即自动 RequireAuthorization；`AddNexMeshAuth` 内置 AddAuthentication/AddAuthorization，业务零心智；
- **审批中心收紧**：`MapNexMeshApprovals(requireAuthorization: true)`；
- **顺序契约**：`UseNexMeshAuthentication()`（认证）→ `UseAuthorization()`（显式钉住顺序——隐式插入位置不可依赖）→ `UseNexMeshService()`；
- **存储**：Memory 会话默认（单实例），Redis 实现随缓存包交付（`ITokenStore` 接口不变）。


### 9.5 数据层（NexMesh.Data.FreeSql，已实现）

**实体实现约定接口即自动接管**——SPD 时代手工维护的 EntityKeyLongBase/BaseRepository 从此消失：

| 接口 | 自动接管的行为 |
|---|---|
| `ISoftDelete` | 全局过滤自动排除已删记录（忘记加条件的越权读取不可能发生）；`SoftDeleteAsync` 只标记不物理删，`DisableGlobalFilter("nexmesh-soft-delete")` 可穿透审计 |
| `IAudited` | CreateTime/UpdateTime 写入路径自动填充 |
| `IUserAudited` | 操作人从 ICurrentUser 自动填充（离线任务里是快照身份） |
| `IHasConcurrencyStamp` | 更新校验戳，冲突 → 40902/409 |
| long 主键为 0 | 框架雪花 ID 接管（41 位毫秒 + 10 位节点 + 12 位序列，自研无依赖；自增主键走数据库） |

多库：`AddNexMeshDb("main"/"his", ...)` 命名注册（SPD 双库形态），`IDbFactory.Get(name)` 取用。仓储 `IRepository<T>` 提供 Add/Find/Update/SoftDelete/Count/List/Paged，复杂查询直接 IFreeSql（全局过滤同样生效）。Host 总装自动按 `NexMesh:Db` 配置注册 main 库。

### 9.6 双层缓存（NexMesh.Cache，已实现，StackExchange.Redis 统一生态）

决策：Redis 客户端全家统一 **StackExchange.Redis**（与后续 Redis OM 向量层共用连接生态）。

- **双层治理**：L1 进程内存（短 TTL 扛热点）+ L2 Redis（跨实例一致）；`Mode=Auto` 未配连接串自动全内存兜底（零基础设施）；
- **单飞防击穿**：`GetOrCreateAsync` 条带信号量 + 双检查——同 Key 并发只放一个回源（300ms 有界等待，极端争用降级并发回源）；值类型语义正确（default≠未命中，null 不缓存）；
- **分布式锁**：Redis = SET NX PX + Lua 令牌释放（防超时后误解他人锁）；内存模式 = 进程内信号量（防双释放）；
- **幂等防重**：`UseNexMeshIdempotency` + `Idempotency-Key` 头——同 Key 并发重复 409（40901），20s 周期续租防长事务误放行；
- **命名源**：`NexMesh:Cache:Sources[name]`（连接串/前缀/TTL/L1Ttl），`ICacheFactory.Get(name)`。真实 Redis 集成测试按 `NEXMESH_TEST_REDIS` 环境变量门控。

### 9.7 定时调度（NexMesh.Worker，已实现，替代 Hangfire 的轻量自研）

- **双基类**：`NexMeshCronJob`（Cronos 表达式，5 字段/含秒）+ `NexMeshIntervalJob`（TimeSpan 周期）；注册一行：`AddNexMeshCronJob<T>("0 2 * * *")`（每日凌晨 2 点，`runOnStart` 可选）；
- **四纪律**：单轮异常隔离（记录后继续，绝不停摆）；退避等待（到点才醒，不忙轮询）；优雅停机（stoppingToken，在途轮正常收尾）；**每轮经 BackgroundRunner 以系统身份快照执行**（ICurrentUser 读到 `system:{JobName}`，Consumer Span 接续——审计与工具调用同源）；
- **业务只写 `RunOnceAsync(IServiceProvider scoped, ct)`**：依赖从全新作用域解析，严禁闭包捕获 Scoped（三铁律沿用）。

### 9.8 工具箱(NexMesh.Toolbox,已实现)

- **Excel 导入导出**(MiniExcel 零模板):`NexMeshExcel.ExportAsync/ImportAsync<T>` 强类型内存往返;
- **文件存储** `IFileStorage` 抽象(业务只面向接口,OSS 上线换实现零改动):**Db**(事务库 Blob,SPD 形态,操作人自动审计+软删可恢复)| **Local**(磁盘目录);`AddNexMeshFileStorage` 配置切换;AutoApi 返回 `IActionResult`(如 FileStreamResult)按 MVC 语义执行;
- **邮件** `IEmailSender`(SMTP,配置节 NexMesh:Email:Smtp;接口化便于测试与替换);
- **数据脱敏** `Masking`(手机/身份证/邮箱/姓名)。

## 十、P2+ 路线图

~~Semantic Cache & Tool Search~~ ✅ 已交付（9.9）。路线图清零。

### 9.9 语义缓存（NexMesh.AI.SemanticCache，已实现）

用户提问 → 嵌入向量 → 余弦相似检索 → 命中（≥ 阈值）秒回上次回答（跳过模型调用，成本降 50%+，延迟秒级→毫秒级）；未命中 → 调用模型 → 存入向量缓存。

- **装饰器形态**：`SemanticCacheChatClient`（DelegatingChatClient）挂在工厂管道**最内层**——命中时跳过弹性+模型全部成本；
- **双存储**：`InMemorySemanticStore`（暴力余弦，单实例/开发/测试）| `RedisSemanticStore`（FT.SEARCH KNN + HNSW + COSINE，Redis Stack，复用 SER 连接生态）；
- **嵌入**：`ISemanticEmbedder` 接口（业务注册真实 embedding 如 text-embedding-3-small）；`HashEmbedder` 兜底（字符频率→L2 归一化，仅保证去重，无真实语义——开发零依赖）；
- **语义**：仅覆盖非流式调用（流式首 token 无法完整比对，已在弹性层有首 token 守卫）；仅缓存成功响应（空回复不缓存）；TTL 可配置（默认 3600s，0=永久）；
- **接缝**：`ISemanticCacheWrapper`（AI 核心定义、SemanticCache 包实现）——依赖方向正确（AI 不引用 SemanticCache 包），`NexMesh:AI:SemanticCache:Enabled=true` 开关。
