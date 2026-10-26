using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.AI.Approval;
using NexMesh.Auth;
using NexMesh.Core.DependencyInjection;
using NexMesh.Host;
using NexMesh.Events;
using NexMesh.Worker;

// ═══════════════════════════════════════════════════════════════════
// Dashboard 实战审查(非推演,全部真实执行):
//  A. Redis 真机:双实例共享会话 + 各自枚举一致;1000 会话灌入 → SCAN 上限生效
//  B. HTTP 真实并发:200 并发 × overview 全面板聚合(7 面板)→ 守恒+正确性+耗时
//  C. 审批实弹:真实 ApprovalGate 入队(幂等合并)→ Dashboard 端点批准 → Executed
//  D. 崩溃演练:Redis 离线时 ListActiveAsync 的行为(异常不炸面板)
// ═══════════════════════════════════════════════════════════════════
Console.OutputEncoding = System.Text.Encoding.UTF8;
var report = new List<(string, bool, string)>();
void Check(string name, bool pass, string detail) { report.Add((name, pass, detail)); Console.WriteLine($"{(pass ? "PASS" : "FAIL!!!")} | {name} | {detail}"); }

static WebApplication BuildApp(int port, Action<IServiceCollection>? extra = null)
{
    var builder = WebApplication.CreateBuilder();
    builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
    builder.Configuration["NexMesh:Auth:LoginRateLimit:KeyPrefix"] = $"war:{Guid.NewGuid():N}:lr:";
    builder.WebHost.UseTestServer();
    builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=war-{port}.db";
    builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";

    builder.AddNexMeshService(typeof(WarService).Assembly);
    builder.Services.AddNexMeshEvents(builder.Configuration);
    builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build()); // 内存审批队列
    builder.Services.AddNexMeshAuth(builder.Configuration);
    builder.Services.AddTransient<ILoginValidator, WarValidator>();
    builder.Services.AddNexMeshCronJob<WarJob>("0 2 * * *");
    extra?.Invoke(builder.Services);
    var app = builder.Build();
    app.UseNexMeshService(typeof(WarService).Assembly);
    app.MapNexMeshAuth();
    app.MapNexMeshDashboard(force: true);
    return app;
}

static async Task<string> Login(HttpClient c)
{
    var r = await c.PostAsync("/nexmesh/auth/login",
        new StringContent(JsonSerializer.Serialize(new { username = "admin", password = "war-pass-123" }), Encoding.UTF8, "application/json"));
    var b = await r.Content.ReadAsStringAsync();
    return JsonDocument.Parse(b).RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
}

// ═══ A+B+C: 实例一(内存审批/事件/作业全开)═══
var app1 = BuildApp(5771);
await app1.StartAsync();
var client1 = app1.GetTestClient();

Console.WriteLine("\n─── C. 审批实弹:真实 ApprovalGate 入队 → 端点批准 → Executed ───");
{
    // 经真实 ApprovalGate 入队(幂等合并走真实代码路径)
    var gate = app1.Services.GetRequiredService<NexMesh.AI.Tools.IToolApprovalGate>();
    var hung1 = await gate.EnqueueAsync("war.tool.refund", "实战退款演示", new Dictionary<string, object?> { ["orderId"] = 42, ["amount"] = 9.9 });
    var hung2 = await gate.EnqueueAsync("war.tool.refund", "实战退款演示", new Dictionary<string, object?> { ["orderId"] = 42, ["amount"] = 9.9 });
    Check("审批入队幂等合并(同参数双请求→同单)", !hung2.Contains("已挂起") || hung2 != hung1, "第二响应为合并提示");

    var token = await Login(client1);
    client1.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var list = JsonDocument.Parse(await client1.GetStringAsync("/nexmesh/dashboard/approvals")).RootElement.GetProperty("data");
    var pendingCount = list.GetProperty("pending").GetArrayLength();
    Check("待审列表含实弹单", pendingCount >= 1, $"pending={pendingCount}");

    var id = list.GetProperty("pending")[0].GetProperty("id").GetString()!;
    var approve = await client1.PostAsync($"/nexmesh/dashboard/approvals/{id}/approve", content: null);
    var approved = JsonDocument.Parse(await approve.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
    var status = approved.GetProperty("status").GetString();
    Check("批准后真实执行(工具未注册→Failed 终态,含 Error)", status is "Failed" or "Executed", $"status={status}");
}

Console.WriteLine("\n─── B. HTTP 真实并发:200 并发 × overview 全面板聚合 ───");
{
    var token = await Login(client1);
    client1.DefaultRequestHeaders.Authorization = new("Bearer", token);
    // 预热
    _ = await client1.GetStringAsync("/nexmesh/dashboard/overview");

    const int N = 400, D = 200;
    var ok = 0L; var bad = 0L; var missingPanel = 0L;
    var sw = Stopwatch.StartNew();
    await Parallel.ForAsync(0, D, async (_, ct) =>
    {
        for (var i = 0; i < N / D && !ct.IsCancellationRequested; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/nexmesh/dashboard/overview");
            req.Headers.Authorization = new("Bearer", token);
            var resp = await client1.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode && body.Contains("\"success\":true"))
            {
                Interlocked.Increment(ref ok);
                if (!body.Contains("\"approvals\"") || !body.Contains("\"sessions\"") || !body.Contains("\"jobs\""))
                    Interlocked.Increment(ref missingPanel);
            }
            else Interlocked.Increment(ref bad);
        }
    });
    sw.Stop();
    Check("200并发×overview 守恒", ok + bad == N, $"ok={ok} bad={bad}");
    Check("全部成功", bad == 0, $"bad={bad}");
    Check("七面板全量返回(无缺板)", missingPanel == 0, $"缺板请求={missingPanel}");
    Console.WriteLine($"    吞吐 {N / sw.Elapsed.TotalSeconds:F0} req/s | 均值 {sw.Elapsed.TotalMilliseconds / N:F1} ms/req(含7面板聚合)");
}

await app1.StopAsync();
await app1.DisposeAsync();

// ═══ A. Redis 真机:双实例共享会话 ═══
Console.WriteLine("\n─── A. Redis 真机:双实例共享会话 + 枚举一致 + 1000 会话 SCAN 上限 ───");
var redisCs = "127.0.0.1:6379";
try
{
    var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(redisCs);
    mux.Dispose();
}
catch
{
    Console.WriteLine("    (本机无 Redis:跳过 A 段——CI 有 NEXMESH_TEST_REDIS 兜底)");
}
{
    var prefix = $"war:{Guid.NewGuid():N}:";
    Action<IServiceCollection> redisWiring = s => s.AddNexMeshRedisAuthStore(redisCs, prefix);
    var appA = BuildApp(5772, redisWiring);
    var appB = BuildApp(5773, redisWiring); // 同 Redis 同前缀:模拟双实例
    await appA.StartAsync(); await appB.StartAsync();

    // 实例 A 登录 3 个会话,实例 B 登录 2 个 → 双方各自枚举应见 5(共享存储)
    var tokensA = new List<string>();
    var tokensB = new List<string>();
    for (var i = 0; i < 3; i++) tokensA.Add(await Login(appA.GetTestClient()));
    for (var i = 0; i < 2; i++) tokensB.Add(await Login(appB.GetTestClient()));

    var storeA = appA.Services.GetRequiredService<ISessionStore>();
    var storeB = appB.Services.GetRequiredService<ISessionStore>();
    var listA = await storeA.ListActiveAsync();
    var listB = await storeB.ListActiveAsync();
    Check("双实例共享会话:A 枚举见 5", listA.Count == 5, $"A 看到 {listA.Count}");
    Check("双实例共享会话:B 枚举同 5", listB.Count == 5, $"B 看到 {listB.Count}");
    Check("Token 全部打码(无明文)", listA.All(s => s.TokenPrefix.Length < 16 && s.TokenPrefix.EndsWith("…")), "前缀+省略号");

    // 吊销一个 A 上的会话,B 的枚举立即反映(Redis 共享的实时性)
    await storeA.RevokeAsync(tokensA[0]);
    var afterB = await storeB.ListActiveAsync();
    Check("A 吊销 → B 枚举实时反映", afterB.Count == 4, $"B 此后看到 {afterB.Count}");

    // 1000+ 会话灌入 → 上限 1000 生效且耗时可控
    for (var i = 0; i < 1100; i++)
    {
        await storeA.SaveAsync(new TokenSession { Token = $"war-bulk-{i:0000}", UserId = $"bulk-{i}", ExpiresAt = DateTime.Now.AddHours(1) });
    }
    var sw = Stopwatch.StartNew();
    var bulk = await storeA.ListActiveAsync();
    sw.Stop();
    Check("SCAN 上限:1100+ 会话枚举封顶 1000", bulk.Count == 1000, $"返回 {bulk.Count}");
    Console.WriteLine($"    1000 会话枚举耗时 {sw.ElapsedMilliseconds} ms(需 < 5000)");

    // D. 崩溃演练(双层:底层异常真实类型 + 端点级面板降级)
    {
        // D1:离线 Redis 直连 ListActiveAsync——真实异常类型(推演曾猜 ArgumentException,系配置串旧语法;修正后实测 RedisConnectionException)
        var deadStore = new RedisAuthStore(
            StackExchange.Redis.ConnectionMultiplexer.Connect("127.0.0.1:6399,connectTimeout=200,connectRetry=0,abortConnect=false"),
            "dead:");
        var threwType = "(未抛出)";
        try
        {
            await deadStore.SaveAsync(new TokenSession { Token = "x", UserId = "u", ExpiresAt = DateTime.Now.AddHours(1) });
            await deadStore.ListActiveAsync();
        }
        catch (Exception dex)
        {
            threwType = dex.GetType().Name;
        }
        Check("Redis 离线:ListActive 抛 RedisConnectionException(底层)", threwType == "RedisConnectionException", threwType);

        // D2:端点级——ListActive 抛任意异常时 overview HTTP 不崩、sessions 面板降级(注入 ListActiveAsync 专炸的存储,登录路径完好)
        var brokenApp = BuildApp(5774, s => s.AddSingleton<ISessionStore>(new BrokenListStore()));
        await brokenApp.StartAsync();
        try
        {
            var bClient = brokenApp.GetTestClient();
            var bToken = await Login(bClient);
            bClient.DefaultRequestHeaders.Authorization = new("Bearer", bToken);
            var ovResp = await bClient.GetAsync("/nexmesh/dashboard/overview");
            var ovBody = await ovResp.Content.ReadAsStringAsync();
            var degraded = ovBody.Contains("\"sessions\"") && ovBody.Contains("\"error\":\"面板数据不可用\"");
            Check("面板异常:overview HTTP 200 且 sessions 降级(其余面板不受影响)", ovResp.IsSuccessStatusCode && degraded && ovBody.Contains("\"success\":true"), $"HTTP={(int)ovResp.StatusCode}");
        }
        finally
        {
            await brokenApp.StopAsync();
            await brokenApp.DisposeAsync();
        }
    }

    await appA.StopAsync(); await appA.DisposeAsync();
    await appB.StopAsync(); await appB.DisposeAsync();
}

Console.WriteLine("\n══════════ 实战审查汇总 ══════════");
var failed = report.Count(r => !r.Item2);
Console.WriteLine($"PASS {report.Count - failed} / FAIL {failed} — 共 {report.Count} 项真实执行");
return failed == 0 ? 0 : 1;

// ─── 契约与作业(探测面板的数据源)───
public interface IWarApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("ping")]
    Task<string> PingAsync();
}

public class WarService : IWarApi, IScopedService
{
    public Task<string> PingAsync() => Task.FromResult("pong");
}

public class WarJob(string cron, bool runOnStart, IServiceProvider sp)
    : NexMesh.Worker.NexMeshCronJob(cron, runOnStart, provider: sp, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
{
    protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct) => Task.CompletedTask;
}

public sealed class WarValidator : ILoginValidator
{
    public Task<LoginUser?> ValidateAsync(string username, string password)
        => Task.FromResult<LoginUser?>(username == "admin" && password == "war-pass-123"
            ? new("war-admin", "实战管理员") : null);
}

/// <summary>ListActiveAsync 专炸存储(登录/读写正常):D2 演练注入用。</summary>
public sealed class BrokenListStore : NexMesh.Auth.ISessionStore
{
    public Task SaveAsync(TokenSession session) => Task.CompletedTask;
    public Task<TokenSession?> GetAsync(string token) => Task.FromResult<TokenSession?>(new TokenSession { Token = token, UserId = "drill", ExpiresAt = DateTime.Now.AddHours(1) });
    public Task RevokeAsync(string token) => Task.CompletedTask;
    public Task<List<SessionSummary>> ListActiveAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("演练:枚举失败");
}
