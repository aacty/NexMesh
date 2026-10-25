using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// NexMesh HTTP 压测工具(零外部依赖):
//   dotnet run -c Release -- --url http://localhost:5000 --duration 15 --concurrency 50
// 场景:health(GET /health)、contract(POST /api/employee/search,需演示账号登录 doctor/123456)
// 输出:每场景 RPS / p50 / p95 / p99 / 错误率(Markdown)

var url = Args("url", "http://localhost:5000").TrimEnd('/');
var duration = int.Parse(Args("duration", "15"));
var concurrency = int.Parse(Args("concurrency", "50"));

using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
{
    BaseAddress = new Uri(url),
    Timeout = TimeSpan.FromSeconds(30),
};

// 登录取令牌(演示账号;登录端点自带 5 次/分钟限流,仅此一次)
string? token = null;
try
{
    var login = await http.PostAsync("/nexmesh/auth/login",
        new StringContent(JsonSerializer.Serialize(new { username = "doctor", password = "123456" }), Encoding.UTF8, "application/json"));
    if (login.IsSuccessStatusCode)
    {
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
    }
}
catch
{
    // 登录失败(如未启用认证/无种子)时跳过契约场景
}

Console.WriteLine($"# NexMesh HTTP 压测({DateTime.Now:yyyy-MM-dd HH:mm})\n");
Console.WriteLine($"目标:{url} | 时长:{duration}s | 并发:{concurrency}\n");
Console.WriteLine("| 场景 | RPS | p50 | p95 | p99 | 错误率 |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");

await Run("health", "/health", null);
if (token is not null)
{
    await Run("contract(分页查询)", "/api/employee/search?pageIndex=1&pageSize=5", token);
}

Console.WriteLine();

async Task Run(string name, string path, string? bearer)
{
    var latencies = new List<long>();
    var errors = 0L;
    var total = 0L;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));

    if (bearer is not null)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }

    var workers = Enumerable.Range(0, concurrency).Select(async _ =>
    {
        await gate.Task;
        while (!cts.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = path.Contains('?') || path.Contains("search")
                    ? await http.PostAsync(path, content: null)
                    : await http.GetAsync(path);
                sw.Stop();
                Interlocked.Increment(ref total);
                if (!response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref errors);
                }
                else
                {
                    lock (latencies) latencies.Add(sw.ElapsedTicks);
                }
            }
            catch
            {
                sw.Stop();
                Interlocked.Increment(ref total);
                Interlocked.Increment(ref errors);
            }
        }
    }).ToArray();

    gate.SetResult();
    await Task.WhenAll(workers);

    var elapsed = duration; // 固定窗口
    var rps = total / (double)elapsed;
    if (latencies.Count == 0)
    {
        Console.WriteLine($"| {name} | 0 | - | - | - | 100% |");
        return;
    }
    var sorted = latencies.OrderBy(t => t).ToArray();
    double Ms(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 2);
    double P(double p) => Ms(sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))]);
    var errorRate = total == 0 ? 0 : errors * 100.0 / total;
    Console.WriteLine($"| {name} | {rps:F0} | {P(0.50)}ms | {P(0.95)}ms | {P(0.99)}ms | {errorRate:F2}% |");
}

static string Args(string name, string fallback)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals($"--{name}", StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return fallback;
}
