using FreeSql;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Authorization;
using NexMesh.AI.Approval;
using NexMesh.Auth;
using NexMesh.AI.Core;
using NexMesh.AI.Tools;
using NexMesh.Events;
using NexMesh.AiDemo.Entities;
using NexMesh.Host;
using NexMesh.Toolbox;
using NexMesh.Worker;

var builder = WebApplication.CreateBuilder(args);

// —— 框架总装:仅此一行。DI/生命周期/配置/FreeSql/AI 管道/中间件顺序全部隐形 ——
builder.AddNexMeshService();

// 审批门:High 风险工具挂起拦截(AiSettings.json 已开启),审批队列走 FreeSql 持久化
builder.Services.AddNexMeshApproval(builder.Configuration);
builder.Services.AddNexMeshCronJob<NexMesh.AiDemo.Jobs.EmployeeReportJob>("* * * * *"); // 每分钟定时作业演示
builder.Services.AddNexMeshFileStorage(builder.Configuration); // 文件存储:DB Blob(事务库)
builder.Services.AddNexMeshAuth(builder.Configuration);
builder.Services.AddTransient<ILoginValidator, NexMesh.AiDemo.Services.DemoLoginValidator>();
builder.Services.AddNexMeshEvents(builder.Configuration); // 进程内事件总线:订阅者自动发现(含 Events/DemoEventSubscriber)

var app = builder.Build();

// ════════ 以下全部是业务逻辑与 AI 逻辑,不含任何框架管线知识 ════════

// 业务种子:3000 名员工(千级规模的分页/检索/软删实测基线)。IFreeSql 由宿主注入
var fsql = app.Services.GetRequiredService<IFreeSql>();
if (await fsql.Select<Employee>().CountAsync() == 0)
{
    var departments = new[] { "护理部", "信息科", "财务科", "后勤部", "医务部" };
    var batch = new List<Employee>(500);
    for (var i = 1; i <= 3000; i++)
    {
        batch.Add(new Employee
        {
            Id = i,
            Name = $"员工{i:D4}",
            Department = departments[i % departments.Length],
            CreateTime = DateTime.Now,
        });
        if (batch.Count == 500)
        {
            await fsql.Insert(batch).ExecuteAffrowsAsync();
            batch.Clear();
        }
    }
    if (batch.Count > 0)
    {
        await fsql.Insert(batch).ExecuteAffrowsAsync();
    }
}

// —— 框架管线与端点:同样一行 ——
app.UseNexMeshAuthentication(); // 认证在前:Token 声明进入上下文快照/ICurrentUser
app.UseAuthorization();         // 显式钉住顺序:授权必须在认证之后
app.UseNexMeshService();

// AI 工具清单([NexMeshTool] 自动发现)
app.MapGet("/api/ai/tools", (INexMeshToolRegistry tools) =>
    tools.DiscoverTools().Select(t => new { t.Name, t.Description }));

// 流式 AI Chat(SSE)。默认 Provider=stub → 离线可跑;配置真实 Key 后直连 OpenAI/DeepSeek/Ollama
app.MapPost("/api/ai/chat", async (
    ChatRequest req,
    HttpContext http,
    INexMeshChatFactory factory,
    INexMeshToolRegistry tools,
    CancellationToken ct) =>
{
    http.Response.ContentType = "text/event-stream";

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, "你是 NexMesh v2.0 演示助手,可调用已注册业务工具回答问题。"),
        new(ChatRole.User, req.Prompt),
    };
    var options = new ChatOptions
    {
        Tools = [.. tools.DiscoverTools()],
        AllowMultipleToolCalls = false, // 安全约定:审批门(Phase 2)未启用前严禁多工具并发
    };

    await foreach (var update in factory.CreateClient().GetStreamingResponseAsync(messages, options, ct))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            await http.Response.WriteAsync($"data: {update.Text}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
});

// 审批中心:GET /api/approvals/pending、/{id}、POST /{id}/approve、/{id}/reject
app.MapNexMeshAuth(); // 登录/登出/me
app.MapNexMeshApprovals(requireAuthorization: true); // 审批中心:须持有效 Token

// 事件总线演示:发布即返回,订阅者异步处理(日志可见;重试演示观察 nexmesh.events.retried 指标)
app.MapPost("/api/events/demo", async (NexMesh.Events.IEventPublisher publisher) =>
{
    await publisher.PublishAsync("demo:employee:created", new { Name = "演示员工", CreatedAt = DateTime.Now });
    return Results.Ok(new { message = "事件已发布,处理异步进行(见日志)" });
});

// 重试/故障演示:发布后观察日志中的重试与"重试耗尽进故障处理"
app.MapPost("/api/events/flaky", async (NexMesh.Events.IEventPublisher publisher) =>
{
    await publisher.PublishAsync("demo:order:flaky", new { OrderId = 42 });
    return Results.Ok(new { message = "不稳定事件已发布(日志可见 3 次尝试后进故障处理)" });
});

app.Run();

/// <summary>Chat 请求体。</summary>
public record ChatRequest(string Prompt);
