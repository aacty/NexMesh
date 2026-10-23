# NexMesh AI 工具对接指南

让 Claude Desktop、Cursor、GitHub Copilot 等 AI 编码助手理解 NexMesh 框架,写出符合规范的代码。

---

## 一、Claude Desktop

### 方式一:项目级指令(推荐)

把 `.claude/commands/nexmesh.md` 放在项目根目录。Claude 打开项目时自动读取 NexMesh 规范。

### 方式二:MCP Server 配置

在 Claude Desktop 的 `claude_desktop_config.json` 中添加:

```json
{
  "mcpServers": {
    "nexmesh": {
      "command": "npx",
      "args": ["-y", "@anthropic/mcp-fetch"],
      "env": {
        "NEXMESH_BASE_URL": "http://localhost:5000"
      }
    }
  }
}
```

### 方式三:直接粘贴系统提示

把 `.claude/commands/nexmesh.md` 的内容粘贴到 Claude 对话开头,AI 立即理解 NexMesh 规范。

---

## 二、Cursor

Cursor 自动读取项目根目录的 `.cursorrules` 文件:

```
项目根目录/
├── .cursorrules          ← Cursor 自动读取,包含 NexMesh 全部规范
├── .cursor/
│   └── mcp.json          ← MCP Server 配置(可选)
└── src/
```

`.cursorrules` 已包含:实体模式、API 模式、错误码、分页、缓存、中间件顺序等全部规范。

---

## 三、GitHub Copilot

### VS Code / Visual Studio

项目根目录放 `.github/copilot-instructions.md`,Copilot Chat 自动读取:

```
项目根目录/
└── .github/
    └── copilot-instructions.md    ← Copilot 自动读取
```

### Copilot Chat 中使用

在 Chat 中输入:
```
@workspace 按照 NexMesh 规范,帮我写一个商品 CRUD 模块
```

Copilot 会按照 `copilot-instructions.md` 中的规范生成代码。

---

## 四、其他 AI 工具

### 通用方式:llms.txt

项目根目录的 `llms.txt` 是 AI 工具的文档索引(多个 AI 工具支持此标准):

```
项目根目录/
└── llms.txt    ← AI 工具文档索引(11 个包的能力清单+文档链接)
```

### 通用方式:mcp.json

项目根目录的 `mcp.json` 定义了 MCP 工具(诊断/健康检查/API 文档):

```json
{
  "name": "nexmesh",
  "tools": [
    { "name": "query_diagnostics", "httpCall": { "method": "GET", "url": "{baseUrl}/diagnostics" } },
    { "name": "health_check", "httpCall": { "method": "GET", "url": "{baseUrl}/health/{type}" } },
    { "name": "get_api_docs", "httpCall": { "method": "GET", "url": "{baseUrl}/swagger/v1/swagger.json" } }
  ]
}
```

---

## 五、文件清单

| 文件 | 适用工具 | 作用 |
|---|---|---|
| `.claude/commands/nexmesh.md` | Claude Desktop / Claude Code | 项目级指令(NexMesh 规范) |
| `.cursorrules` | Cursor | 自动读取的规则文件 |
| `.github/copilot-instructions.md` | GitHub Copilot | Copilot Chat 指令 |
| `llms.txt` | 通用 | AI 文档索引 |
| `mcp.json` | 通用 | MCP 工具定义 |

---

## 六、验证 AI 是否理解了规范

问 AI 以下问题,如果回答正确说明配置生效:

1. "NexMesh 怎么定义实体?" → 应该说继承 ISoftDelete/IAudited 等接口
2. "怎么写分页查询?" → 应该用 OrderByWhitelist + ToPagedResultAsync
3. "业务异常怎么处理?" → 应该说 throw new MeshException,不需要 try-catch
4. "缓存怎么用?" → 应该说 GetOrCreateAsync 读 + RemoveAsync 写后清
5. "中间件顺序?" → 应该说 TraceId → 异常处理 → 认证 → 授权 → 端点

---

## 七、自定义扩展

### 添加项目特有的规范

编辑 `.claude/commands/nexmesh.md` 或 `.cursorrules`,在末尾追加:

```markdown
## 项目特有规范

### 数据库
- 所有表名用复数(orders, products)
- 主键用 UUIDv7,不用自增

### 业务规则
- 订单金额不能超过 100 万
- VIP 客户享有 9 折优惠
```

### 添加新的 MCP 工具

编辑 `mcp.json`,在 `tools` 数组中添加:

```json
{
  "name": "query_orders",
  "description": "查询订单列表",
  "inputSchema": {
    "type": "object",
    "properties": {
      "baseUrl": { "type": "string" },
      "status": { "type": "string", "enum": ["pending", "paid", "shipped"] }
    }
  },
  "httpCall": {
    "method": "GET",
    "url": "{baseUrl}/api/orders?status={status}"
  }
}
```
