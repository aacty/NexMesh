using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NexMesh.Auth;

const int TOTAL = 10_000_000;
int DEGREE = Environment.ProcessorCount * 4;

Console.WriteLine($"机器: {Environment.ProcessorCount} 逻辑核 | 目标: {TOTAL:N0} 操作 | 并行度: {DEGREE}");
Console.WriteLine();

{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = int.MaxValue.ToString(),
        ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "3600",
    }).Build();
    var limiter = new LoginRateLimiter(config);
    long passed = 0;
    long issued = 0;
    var sw = Stopwatch.StartNew();
    await Parallel.ForAsync(0, DEGREE, async (w, ct) =>
    {
        var ctx = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Loopback } };
        long taken;
        while ((taken = Interlocked.Add(ref issued, 1000) - 1000) < TOTAL)
        {
            var batch = (int)Math.Min(1000, TOTAL - taken);
            for (var i = 0; i < batch; i++)
            {
                if (await limiter.TryPassAsync(ctx)) Interlocked.Increment(ref passed);
            }
        }
    });
    sw.Stop();
    Console.WriteLine($"[1] LoginRateLimiter 内存计数器");
    Console.WriteLine($"    {TOTAL:N0} 次交错 TryPass:放行 {passed:N0}(应为 {TOTAL:N0})");
    Console.WriteLine($"    守恒: {(passed == TOTAL ? "PASS" : "FAIL!!!")} | 吞吐 {TOTAL / sw.Elapsed.TotalSeconds:N0} ops/s | {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine();
}

{
    var channel = System.Threading.Channels.Channel.CreateBounded<NexMesh.Events.EventWorkItem>(
        new System.Threading.Channels.BoundedChannelOptions(8192)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });

    long consumed = 0;
    var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        await foreach (var _ in channel.Reader.ReadAllAsync())
        {
            Interlocked.Increment(ref consumed);
        }
    })).ToArray();

    var sw = Stopwatch.StartNew();
    long issued = 0;
    await Parallel.ForAsync(0, DEGREE, async (w, ct) =>
    {
        long taken;
        while ((taken = Interlocked.Add(ref issued, 1000) - 1000) < TOTAL)
        {
            var batch = (int)Math.Min(1000, TOTAL - taken);
            for (var i = 0; i < batch; i++)
            {
                await channel.Writer.WriteAsync(new NexMesh.Events.EventWorkItem("c", null, null, DateTimeOffset.UtcNow), ct);
            }
        }
    });
    channel.Writer.TryComplete();
    await Task.WhenAll(consumers);
    sw.Stop();
    Console.WriteLine($"[2] 事件总线有界 Channel(容量 8192,4 消费者,FullMode.Wait)");
    Console.WriteLine($"    {TOTAL:N0} 次并发写:消费 {consumed:N0}(应为 {TOTAL:N0})");
    Console.WriteLine($"    守恒: {(consumed == TOTAL ? "PASS" : "FAIL!!!")} | 吞吐 {TOTAL / sw.Elapsed.TotalSeconds:N0} ops/s | {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine();
}

{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "3",
        ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "3600",
    }).Build();
    var limiter = new LoginRateLimiter(config);
    const int Ips = 1_000_000, PerIp = 5;
    long allowed = 0;
    var sw = Stopwatch.StartNew();
    await Parallel.ForAsync(0, Ips, async (ip, ct) =>
    {
        var addr = new IPAddress(new byte[] { 10, (byte)(ip / 65536), (byte)(ip % 65536 / 256), (byte)(ip % 256) });
        var ctx = new DefaultHttpContext { Connection = { RemoteIpAddress = addr } };
        for (var i = 0; i < PerIp; i++)
        {
            if (await limiter.TryPassAsync(ctx)) Interlocked.Increment(ref allowed);
        }
    });
    sw.Stop();
    Console.WriteLine($"[3] 限流多 Key 分区隔离(1,000,000 独立 IP × 5 次,限额 3)");
    Console.WriteLine($"    放行 {allowed:N0}(应为 {Ips * 3:N0})");
    Console.WriteLine($"    分区正确: {(allowed == Ips * 3 ? "PASS" : "FAIL!!!")} | {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine();
}

{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var before = GC.GetTotalMemory(true);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "3",
        ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "3600",
    }).Build();
    var limiter = new LoginRateLimiter(config);
    for (var i = 0; i < 1_000_000; i++)
    {
        var ctx = new DefaultHttpContext { Connection = { RemoteIpAddress = new IPAddress(new byte[] { 192, (byte)(i / 65536), (byte)(i % 65536 / 256), (byte)(i % 256) }) } };
        await limiter.TryPassAsync(ctx);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var after = GC.GetTotalMemory(true);
    Console.WriteLine($"[4] 内存水位:100 万唯一 Key 注入后净增 {(after - before) / 1024.0 / 1024.0:F1} MB");
    Console.WriteLine("    (窗口过期后 Key 仍驻留字典,无清扫机制——慢速假 IP 扫描将持续增长内存)");
}
Console.WriteLine();
Console.WriteLine("=== 千万级并发审计完成 ===");
// ─── 4. 泄漏验证:短窗口注入 100 万 Key → 过期 → 清扫后内存回落 ───
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var before = GC.GetTotalMemory(true);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "3",
        ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "2", // 短窗口
    }).Build();
    var limiter = new LoginRateLimiter(config);
    for (var i = 0; i < 1_000_000; i++)
    {
        var ctx = new DefaultHttpContext { Connection = { RemoteIpAddress = new IPAddress(new byte[] { 172, (byte)(i / 65536), (byte)(i % 65536 / 256), (byte)(i % 256) }) } };
        await limiter.TryPassAsync(ctx);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var peak = GC.GetTotalMemory(true);
    Console.WriteLine($"[4] 泄漏验证:100 万唯一 Key 注入(窗口 2s)");
    Console.WriteLine($"    注入后驻留 {(peak - before) / 1024.0 / 1024.0:F1} MB");

    await Task.Delay(TimeSpan.FromSeconds(5)); // 全部窗口过期
    for (var i = 0; i < 4200; i++) // 触发摊销清扫
    {
        var ctx = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Loopback } };
        await limiter.TryPassAsync(ctx);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var after = GC.GetTotalMemory(true);
    var reclaimed = (peak - after) / 1024.0 / 1024.0;
    Console.WriteLine($"    过期+清扫后驻留 {(after - before) / 1024.0 / 1024.0:F1} MB(回收 {reclaimed:F1} MB)");
    Console.WriteLine($"    清扫有效: {(reclaimed > 100 ? "PASS(内存回落)" : "FAIL(仍驻留)")}");
}
Console.WriteLine();
Console.WriteLine("=== 千万级并发审计完成 ===");
