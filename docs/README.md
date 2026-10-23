# NexMesh 文档中心

所有文档一览。按角色和场景分类,找到你需要的。

---

## 新手入门

| 文档 | 说明 | 适合谁 |
|---|---|---|
| [README](../README.md) | 25 个功能项完整教程,从零到部署 | 所有人(第一个看的文档) |
| [超大型项目实战指南](超大型项目实战指南.md) | 16 章从零搭建企业级订单系统(完整可跟做) | 要开始新项目的开发者 |
| [实战指南目录](超大型项目实战指南.md#目录) | 实战指南 16 章快速跳转 | 想跳到特定章节的人 |

---

## 开发规范(17 篇,PR 合并门禁)

| 编号 | 规范 | 核心内容 |
|---|---|---|
| 00 | [规范总览](standards/00-规范总览.md) | 等级定义(强制/建议)+ 四条总原则 |
| 01 | [分层与架构](standards/01-分层与架构规范.md) | 垂直切片目录结构、Program.cs 接线、中间件顺序 |
| 02 | [契约设计](standards/02-契约设计规范.md) | 路由命名、DTO 纪律、分页参数、幂等键 |
| 03 | [输入校验](standards/03-输入校验规范.md) | FluentValidation(格式)与 MeshException(业务)的边界 |
| 04 | [错误处理与错误码](standards/04-错误处理与错误码规范.md) | 异常分类、5 位错误码段位规则、登记表制度 |
| 05 | [ID 生成](standards/05-ID生成规范.md) | 雪花 vs UUIDv7 决策表、WorkerId 分配 |
| 06 | [事务与一致性](standards/06-事务与一致性规范.md) | UnitOfWork、软删除、事件最终一致、分布式锁 |
| 07 | [缓存与 Redis](standards/07-缓存与Redis规范.md) | Key 三段式、TTL 必填、Cache-Aside、防击穿 |
| 08 | [身份认证与授权](standards/08-身份认证与授权规范.md) | JWT/ApiKey 选择、密钥管理、端点保护 |
| 09 | [事件总线](standards/09-事件总线规范.md) | 事件命名、发布纪律、消费幂等 |
| 10 | [数据访问](standards/10-数据访问规范.md) | 仓储 vs 直查边界、多数据源、FreeSql 泄漏控制 |
| 11 | [可观测性](standards/11-可观测性规范.md) | 结构化日志、TraceId 三处同源、告警 |
| 12 | [配置管理](standards/12-配置规范.md) | NexMesh:* 节命名、密钥注入、启动验证 |
| 13 | [异步与并发](standards/13-异步与并发规范.md) | async 纪律、CT 透传、批量操作 |
| 14 | [部署](standards/14-部署规范.md) | 镜像、探针、迁移、发布节奏 |
| 15 | [上架检查清单](standards/15-上架检查清单.md) | 合并/上线前 6 组自检项 |
| 16 | [数据库迁移](standards/16-数据库迁移规范.md) | SQL 脚本权威、AutoSyncStructure 边界 |

---

## 配置与部署

| 文档 | 说明 | 适合谁 |
|---|---|---|
| [配置指南](配置指南.md) | 5 种方式:.env / User Secrets / 简化环境变量 / json / Key Vault | 所有开发者 |
| [部署指南](部署指南.md) | 6 种方式:本地 / Windows 服务 / IIS / systemd / Docker / K8s | 开发者 + 运维 |
| [设计决策与路线图](设计决策与路线图.md) | 技术选型原因(FastEndpoints vs Minimal API)、版本规划 | 架构师 |
| [错误码登记表](errorcodes.md) | 框架保留码 + 段位规则 + 服务自定义区 | 所有开发者 |

---

## 排障

| 文档 | 说明 | 适合谁 |
|---|---|---|
| [故障排查指南](故障排查.md) | 日志在哪、TraceId 排障、12 个常见坑(含解法) | 遇到问题的人 |

---

## AI 工具对接

| 文档 | 说明 | 适合谁 |
|---|---|---|
| [AI 工具对接指南](AI工具对接指南.md) | Claude/Cursor/Copilot 配置方法 + 验证清单 | 用 AI 写代码的人 |
| [llms.txt](../llms.txt) | AI 文档索引(11 个包能力清单) | AI 工具自动读取 |
| [mcp.json](../mcp.json) | MCP 工具定义(诊断/健康/API 文档) | 支持 MCP 的工具 |
| [.claude/commands](../.claude/commands/nexmesh.md) | Claude 项目级指令(NexMesh 全规范) | Claude 用户 |
| [.cursorrules](../.cursorrules) | Cursor 自动读取规则 | Cursor 用户 |
| [.github/copilot-instructions](../.github/copilot-instructions.md) | Copilot Chat 指令 | Copilot 用户 |

---

## 包 API 文档(11 个包)

| 包 | README | 核心能力 |
|---|---|---|
| ⚡ Core | [README](../src/NexMesh.Core/README.md) | 统一响应、异常、健康检查、TraceId、限流、ICurrentUser、自动API |
| 🗄️ Data.FreeSql | [README](../src/NexMesh.Data.FreeSql/README.md) | 仓储、软删除、审计、并发、租户、分页、雪花ID |
| 🔄 Data.Migrations | [README](../src/NexMesh.Data.Migrations/README.md) | SQL 脚本迁移 |
| 💾 Cache.FreeRedis | [README](../src/NexMesh.Cache.FreeRedis/README.md) | 缓存、防击穿、分布式锁、幂等 |
| 🔐 Auth | [README](../src/NexMesh.Auth/README.md) | JWT、ApiKey |
| 📨 Events.CAP | [README](../src/NexMesh.Events.CAP/README.md) | 事件总线(Memory/RabbitMQ) |
| 📊 Observability | [README](../src/NexMesh.Observability/README.md) | Serilog、OpenTelemetry |
| 🛡️ Resilience | [README](../src/NexMesh.Resilience/README.md) | HTTP 弹性(重试/熔断) |
| 🔄 Mapping.Mapster | [README](../src/NexMesh.Mapping.Mapster/README.md) | 对象映射 |
| 🧰 Toolbox | [README](../src/NexMesh.Toolbox/README.md) | 雪花ID、脱敏、Excel |
| 📖 Web.Scalar | [README](../src/NexMesh.Web.Scalar/README.md) | API 文档 UI |

---

## 示例代码

| 示例 | 说明 |
|---|---|
| [ReferenceService](../samples/ReferenceService) | 全功能演示(3 模块:订单/商品/客户,21 个端点) |
| [ReferenceService README](../samples/ReferenceService/README.md) | API 一览 + curl 验证 + 项目结构 |
| [脚手架模板](../templates/NexMesh.Templates) | dotnet new install ../templates/NexMesh.Templates/content && dotnet new nexmesh-ai 生成标准 AI 服务(含认证+审批门+工具;模板 csproj 用 NuGet 包引用,需先发布 2.0.0 包) |

---

## 按角色快速入口

### 我是新人
1. [README](../README.md) → 从"第一步:跑起来"开始
2. [实战指南](超大型项目实战指南.md) → 跟做一遍
3. [规范 00-01](standards/00-规范总览.md) → 了解团队约定
4. [故障排查](故障排查.md) → 遇到问题再查

### 我是后端开发者
1. [README](../README.md) → 查 API 用法
2. [包 README](#包-api-文档11-个包) → 用到哪个包看哪个
3. [规范 02-13](standards/02-契约设计规范.md) → 写代码前过一遍
4. [配置指南](配置指南.md) → 本地开发环境搭建

### 我是运维
1. [部署指南](部署指南.md) → 6 种部署方式
2. [故障排查](故障排查.md) → 日志/错误码/常见坑
3. [diagnostics 端点](部署指南.md#运维端点) → 实时监控

### 我用 AI 写代码
1. [AI 工具对接](AI工具对接指南.md) → 配置 AI 助手
2. [.cursorrules](../.cursorrules) → 复制到项目
3. [llms.txt](../llms.txt) → AI 自动读取

---

## 文件结构

```
NexMesh/
├── README.md                          ← 主文档(25 步教程)
├── llms.txt                           ← AI 文档索引
├── mcp.json                           ← MCP 工具定义
├── .cursorrules                       ← Cursor 规则
├── .claude/commands/nexmesh.md        ← Claude 指令
├── .github/copilot-instructions.md    ← Copilot 指令
├── docs/
│   ├── README.md                      ← 本文件(文档中心)
│   ├── 超大型项目实战指南.md            ← 16 章实战
│   ├── 部署指南.md                     ← 6 种部署
│   ├── 配置指南.md                     ← 5 种配置
│   ├── 故障排查.md                     ← 12 个常见坑
│   ├── AI工具对接指南.md               ← AI 助手配置
│   ├── 设计决策与路线图.md              ← 技术选型
│   ├── errorcodes.md                  ← 错误码登记表
│   └── standards/                     ← 17 篇开发规范
│       ├── 00-规范总览.md
│       ├── 01-分层与架构规范.md
│       ├── ...
│       └── 16-数据库迁移规范.md
├── src/                               ← 11 个包(各有 README)
├── samples/ReferenceService/          ← 全功能 Demo
└── templates/                         ← dotnet new 脚手架
```
