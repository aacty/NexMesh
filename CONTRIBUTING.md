# 贡献指南

## 开发环境

- .NET SDK 10(全部目标 net10.0)
- 仓库自带 `NuGet.config`(单源),不依赖个人全局配置

## 开发流程

1. 从 `master` 拉特性分支:`feat/<包名或主题>`
2. 改动约束:
   - `dotnet build` **零警告**(仓库开启 `TreatWarningsAsErrors`)
   - 新能力必须有对应测试 + 包 README + `samples` 或独立示例
   - 公共 API 全部带 XML 注释(便于生成文档)
3. 提交前:`dotnet test NexMesh.slnx`
4. PR 合入 `master`,CI(build + test)必须绿

## 包实现规范(P1 起生效)

- 每个包独立可用,禁止包间横向依赖(共享类型放 `NexMesh.Core`)
- 注册入口命名:`AddNexMeshXxx` / `UseNexMeshXxx` / `MapNexMeshXxx`
- 默认零基础设施:外部依赖(Redis/MQ/DB)不可用时有内存降级或明确的快速失败,由配置控制
- 配置节统一前缀 `NexMesh:*`,启动时验证(严格模式)
- 第三方类型(FreeSql、CAP 等)不得出现在契约/接口签名中,保证可替换

## 版本与发布

- 版本集中在 `Directory.Build.props`(所有包同版本号,简化内部管理)
- 每次发布更新 `CHANGELOG.md`
- 发布脚本:`tools/` 目录维护(规划中)
