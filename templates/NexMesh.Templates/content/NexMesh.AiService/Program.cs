using NexMesh.AiService.Features.Orders;
using NexMesh.AiService.Tools;
using NexMesh.AI.Approval;
using NexMesh.Auth;
using NexMesh.Host;

var builder = WebApplication.CreateBuilder(args);

// ════════ 一行总装:DI/生命周期/配置/FreeSql/AI 管道/缓存全部隐形 ════════
builder.AddNexMeshService();

// 认证 + 审批门 + 工作流
builder.Services.AddNexMeshAuth(builder.Configuration);
builder.Services.AddTransient<ILoginValidator, DemoLoginValidator>();
builder.Services.AddNexMeshApproval(builder.Configuration);
builder.Services.AddNexMeshWorkflow(builder.Configuration);

var app = builder.Build();

// ════════ 管道:认证→授权→上下文快照→异常→CORS→AutoApi→健康→文档 ════════
app.UseNexMeshAuthentication();
app.UseAuthorization();
app.UseNexMeshService();
app.MapNexMeshAuth();
app.MapNexMeshApprovals(); // 默认 requireAuthorization=true
app.Run();
