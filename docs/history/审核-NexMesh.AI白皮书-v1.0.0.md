# 审核报告：NexMesh.AI 架构设计白皮书 v1.0.0

- **审核日期**：2026-03-30
- **审核结论**：**有条件通过（Conditional Pass）**
- **评审意见**：新框架方向正确，薄封装 M.E.AI + Agent Framework、REPR + [NexMeshTool] 声明式工具、审批门及黑盒试点关卡予以全部保留。

## 7 处核心硬伤与修正

1. **M.E.AI API 现代化对齐**：全面升级为 `GetStreamingResponseAsync` / `ChatResponse` / `ChatResponseUpdate`，废弃旧版 API。
2. **定位演进**：从原有 NexMesh 存量生态剥离，采用完全独立、标准技术栈（.NET 10 + FastEndpoints + FreeSql + M.E.AI）的 Green-field 重构。
3. **数据栈统一**：核心数据持久化明确采用 **FreeSql + pgvector/Redis**，彻底消除历史 FreeSql 与 EF Core 混用矛盾。
4. **审批门语义闭环**：引入终局挂起信号、幂等键及审批队列落库机制。
5. **Tool Search 降级与加固**：Foundry 依赖属于未来可选项。Phase 1 不实现动态语义召回，而是通过 MaxToolsPerRequest（默认截断至 10 个以内）作为防 Prompt 膨胀的硬编码兜底策略。真正的本地 Embedding 向量/关键字混合召回作为 Phase 3 的正式交付物。
6. **Guardrails 模块化**：补齐工具输出不可信标记（Prompt Injection 防线）与过滤 Hook。
7. **配置驱动改造**：废弃硬编码 Lambda，全面采用 `NexMesh:AI` 强类型配置节驱动工厂动态组装。

## 3 条路线调整

- **OTel gen_ai 埋点前置**至 Phase 1。
- **P4 增补批量离线 AI 任务语义**（分片/断点续跑）。
- **黑盒试点保留**，首个真实消费者由新项目业务团队选取（存量 Nursing 不迁移）。
