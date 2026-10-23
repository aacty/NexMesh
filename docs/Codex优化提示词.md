# NexMesh Codex 全包优化提示词

> 复制下面整段给 Codex / ChatGPT / Claude 等任何 AI 编码工具,它会按 4 个阶段优化全部 11 个包。

---

## 完整提示词(一键复制)

````markdown
# 任务:NexMesh 框架全包优化

## 项目上下文

NexMesh 是 .NET 10 企业级 Web API 框架,11 个独立 NuGet 包,162 个测试全绿。
仓库根目录的 `llms.txt` 有完整文档索引,先读一遍了解全貌。

### 技术栈
- .NET 10(唯一目标框架)
- FastEndpoints 8.3 / FreeSql 3.5 / Serilog / CAP 10 / xUnit

### 核心约束(违反 = 返工)
1. 第三方类型(FreeSql/CAP/Redis)不出现在公共 API 签名和 DTO 中
2. `dotnet build` 零警告(TreatWarningsAsErrors=true)
3. `dotnet test NexMesh.slnx` 全绿才能交付
4. 不改变已有 public API 的签名(破坏性变更需说明理由)
5. 中间件顺序不变:TraceId → ApiVersion → Idempotency → Audit → ExceptionHandling → Auth → RateLimiter → Endpoints

---

## 工作流程(严格按顺序)

### Phase 1:阅读(不改代码)

逐包阅读源码和测试:

| 包 | 源码位置 | 重点文件 |
|---|---|---|
| Core | src/NexMesh.Core/ | NexMeshAutoApi.cs, NexMeshDiagnostics.cs, NexMeshAudit.cs, ExceptionHandlingMiddleware.cs, NexMeshEnvFile.cs |
| Data.FreeSql | src/NexMesh.Data.FreeSql/ | IRepository.cs, NexMeshDbServiceCollectionExtensions.cs, NexMeshPagingExtensions.cs |
| Data.Migrations | src/NexMesh.Data.Migrations/ | NexMeshMigrations.cs |
| Cache.FreeRedis | src/NexMesh.Cache.FreeRedis/ | RedisCacheSource.cs, MemoryCacheSource.cs, NexMeshIdempotency.cs |
| Auth | src/NexMesh.Auth/ | NexMeshJwt.cs, NexMeshApiKey.cs |
| Events.CAP | src/NexMesh.Events.CAP/ | NexMeshEvents.cs |
| Observability | src/NexMesh.Observability/ | NexMeshObservability.cs |
| Resilience | src/NexMesh.Resilience/ | NexMeshResilience.cs |
| Mapping.Mapster | src/NexMesh.Mapping.Mapster/ | NexMeshMapping.cs |
| Toolbox | src/NexMesh.Toolbox/ | NexMeshIds.cs, NexMask.cs, NexExcel.cs |
| Web.Scalar | src/NexMesh.Web.Scalar/ | NexMeshDocsExtensions.cs |

同时读 tests/ 下对应的测试项目。

### Phase 2:分析(输出报告,不改代码)

按 6 个维度检查每个包:

1. **正确性**:并发(静态字段线程安全/AsyncLocal/锁语义)、边界(null/空集合/溢出/类型转换)、异常路径(HasStarted 后二次写入)
2. **异步完整性**:无 `.Result`/`.Wait()`/`GetAwaiter().GetResult()` 在请求路径、CT 完整透传、无 async void
3. **内存安全**:无无界集合增长、Timer/事件订阅正确取消、Singleton 不持 Scoped(除非有意识)
4. **安全**:SQL 注入(拼接值参数化)、密钥泄露(日志/异常消息)、时序攻击(ConstantTimeEquals)
5. **性能**:反射热点路径(每次请求都反射)、不必要分配(热路径 LINQ/字符串拼接)
6. **API 契约**:XML 注释与行为一致、参数默认值与文档一致、错误响应统一(ApiResult JSON)

输出格式:
```
| 严重度 | 包名 | 文件:行号 | 问题 | 触发场景 | 建议修法 |
|---|---|---|---|---|---|
| P0 | Cache.FreeRedis | RedisCacheSource.cs:45 | ... | ... | ... |
```

### Phase 3:修复(按 P0 → P1 → P2)

每个修复:
1. 修改源码
2. 写回归测试(证明修复前失败、修复后通过)
3. 运行 `dotnet test` 确认全绿
4. 更新对应包的 README(如果 public API 行为变化)

### Phase 4:验证

```bash
dotnet build NexMesh.slnx -c Release    # 零警告
dotnet test NexMesh.slnx -c Release     # 全绿
```

如果条件允许,启动 Demo 验证:
```bash
dotnet run --project samples/ReferenceService
# 访问 /health /diagnostics /scalar 确认正常
```

---

## 各包的具体优化方向

### NexMesh.Core
- AutoApi:参数默认值生效(param.HasDefaultValue)、ValueTask 支持、同步 MeshException 包装问题
- Diagnostics:路由指标改用路由模板(非原始 Path)、/diagnostics 排除自身指标
- Audit:审计 Store 写入失败时记日志(当前静默吞)、慢请求补 User/Tenant
- EnvFile:.env 优先级确认、MapKey 映射表与配置指南一致

### NexMesh.Data.FreeSql
- 乐观并发:check-then-act 改为 UPDATE WHERE stamp=old(真原子 CAS)
- GetPrimaryKey:每次反射 → 缓存 PropertyInfo(静态字典)
- 雪花 ID 初始化:与 Toolbox 共享初始化门(防止覆盖用户 WorkerId)
- 审计 CreatedBy:回退链与 ICurrentUser.Id 优先级统一

### NexMesh.Cache.FreeRedis
- RedisLock.Dispose:fire-and-forget 确认无异常泄露
- GetOrCreateAsync:争锁 300ms 超时后回退直接执行(文档化)
- 内存缓存:分片信号量不可重入(文档化)
- 幂等中间件:锁 TTL 与请求超时的关系

### NexMesh.Auth
- JWT:TokenValidationParameters 与 ITokenService 共享快照确认
- ApiKey:SHA-256 摘要比对的 FirstOrDefault 短路(可泄露命中序号)

### NexMesh.Events.CAP
- MemoryEventBus:OCE(取消)不应记为 Error
- 处理器顺序:是否需要显式排序支持

### NexMesh.Observability / Resilience / Mapping / Toolbox / Web.Scalar
- Resilience 默认值:重试 3 次(非 5)、熔断 5 秒(非 30)
- Toolbox:NexMask 非 11 位手机号兜底、NexExcel 公式注入防护(=/+/-/@ 开头转义)

---

## 输出要求

1. Phase 2 的分析报告(问题清单)
2. Phase 3 的修复清单(每个修复附测试名)
3. 最终验证结果(build/test)
4. 未修复的 P2 项列出原因

## 不要做的事
- 不要重构(只修问题,不改结构)
- 不要删除现有测试
- 不要引入新的 NuGet 包
- 不要改变 public API 签名
- 不要修改 docs/ 下的文档(代码优先,文档后面单独更新)
````

---

## 单包优化提示词(按需使用)

只需替换 `{包名}` 和 `{重点文件}`:

````markdown
请优化 src/NexMesh.{包名}/ 包:

1. 先读 src/NexMesh.{包名}/ 下所有 .cs 文件
2. 再读 tests/NexMesh.{包名}.Tests/ 了解现有测试
3. 重点检查:
   - {重点文件1}:有没有 {具体问题1}
   - {重点文件2}:有没有 {具体问题2}
4. 发现问题就修复,每个修复配套写回归测试
5. 确保 dotnet build 零警告,dotnet test 全绿
6. 更新 src/NexMesh.{包名}/README.md(如果行为变化)
````

---

## Bug 修复提示词

````markdown
Bug:{描述问题}

复现步骤:
1. {步骤1}
2. {步骤2}
3. 实际结果:{错误行为}

可能原因:{你的猜测}

请:
1. 读取 {文件路径} 的 {方法名}
2. 找到根因并修复
3. 写回归测试:修复前失败、修复后通过
4. 确保现有测试不 break
````

---

## 添加新功能提示词

````markdown
给 src/NexMesh.{包名}/ 添加 {功能名称}:

需求:
- {需求1}
- {需求2}
- {需求3}

实现要求:
1. 创建 {文件路径}
2. 参考现有的 {参考文件} 写法
3. 配置节:NexMesh:{节名}
4. 写测试:{测试文件路径}
5. 更新 README 加使用说明
6. dotnet test 全绿
````
