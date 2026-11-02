namespace NexMesh.AI.Approval.Tests;

using FreeSql;
using NexMesh.AI.Approval;

/// <summary>
/// 审批终态历史查询(ListHistoryAsync 默认接口方法):排序/上限/Pending 排除/
/// 自定义实现未覆写返回空表(零破坏)/FreeSql 实现真库查询。
/// </summary>
public class ApprovalHistoryTests
{
    private static ApprovalRequest Make(string tool, ApprovalStatus status, DateTime decidedAt) => new()
    {
        ToolName = tool,
        Status = status,
        CreatedAt = decidedAt.AddMinutes(-5),
        DecidedAt = status == ApprovalStatus.Pending ? null : decidedAt,
        DecidedBy = status == ApprovalStatus.Pending ? null : "admin",
    };

    [Fact]
    public async Task 终态进历史_Pending不进_按决定时间倒序_limit生效()
    {
        var store = new InMemoryApprovalStore();
        await store.AddAsync(Make("WaitTool", ApprovalStatus.Pending, DateTime.Now.AddMinutes(-1)));
        await store.AddAsync(Make("OldTool", ApprovalStatus.Executed, DateTime.Now.AddHours(-2)));
        await store.AddAsync(Make("RecentTool", ApprovalStatus.Rejected, DateTime.Now));
        await store.AddAsync(Make("MidTool", ApprovalStatus.Failed, DateTime.Now.AddMinutes(-30)));

        var history = await store.ListHistoryAsync();

        Assert.Equal(3, history.Count); // Pending 不进
        Assert.Equal("RecentTool", history[0].ToolName); // 倒序:最新决定在前
        Assert.Equal("MidTool", history[1].ToolName);
        Assert.Equal("OldTool", history[2].ToolName);

        var limited = await store.ListHistoryAsync(limit: 2);
        Assert.Equal(2, limited.Count);
        Assert.Equal("RecentTool", limited[0].ToolName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task limit非正_契约返回空表_内存与真库一致(int limit)
    {
        // 内存实现:LINQ Take(≤0) 天然空表;真库实现:FreeSql Take(≤0) 忽略 LIMIT 返回全表(SSS+审核实弹坐实),须守卫
        var memory = new InMemoryApprovalStore();
        await memory.AddAsync(Make("DoneTool", ApprovalStatus.Executed, DateTime.Now.AddMinutes(-1)));
        Assert.Empty(await memory.ListHistoryAsync(limit));

        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-approval-limit-{Guid.CreateVersion7():N}.db");
        try
        {
            using var db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
                .UseAutoSyncStructure(true)
                .Build();
            var store = new FreeSqlApprovalStore(db);
            await store.AddAsync(Make("DoneTool", ApprovalStatus.Executed, DateTime.Now.AddMinutes(-1)));
            Assert.Empty(await store.ListHistoryAsync(limit));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
        }
    }

    /// <summary>只实现五个必需成员的最小 store——默认接口方法语义:未覆写返回空表(自定义实现者零破坏)。</summary>
    private sealed class MinimalStore : IApprovalStore
    {
        public Task<ApprovalRequest> AddAsync(ApprovalRequest request) => Task.FromResult(request);
        public Task<ApprovalRequest?> GetAsync(string id) => Task.FromResult<ApprovalRequest?>(null);
        public Task<List<ApprovalRequest>> ListPendingAsync() => Task.FromResult(new List<ApprovalRequest>());
        public Task<ApprovalRequest> UpdateAsync(ApprovalRequest request) => Task.FromResult(request);
        public Task<ApprovalRequest?> FindPendingByIdempotencyKeyAsync(string idempotencyKey) => Task.FromResult<ApprovalRequest?>(null);
    }

    [Fact]
    public async Task 默认接口方法_自定义实现未覆写_返回空表()
    {
        // DIM 语义:默认方法体只能经接口类型调用(直接类上调用不编译)——这正是"未覆写也有合理默认"的机制
        IApprovalStore store = new MinimalStore();
        var history = await store.ListHistoryAsync();
        Assert.Empty(history);
    }

    [Fact]
    public async Task FreeSql实现_终态历史_真库查询()
    {
        // 教训沿用:SQLite :memory: 连接池下每连接独立 → 文件库
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-approval-history-{Guid.CreateVersion7():N}.db");
        try
        {
            using var db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
                .UseAutoSyncStructure(true)
                .Build();
            var store = new FreeSqlApprovalStore(db);

            await store.AddAsync(Make("WaitTool", ApprovalStatus.Pending, DateTime.Now.AddMinutes(-1)));
            await store.AddAsync(Make("OldTool", ApprovalStatus.Executed, DateTime.Now.AddHours(-2)));
            await store.AddAsync(Make("RecentTool", ApprovalStatus.Rejected, DateTime.Now));

            var history = await store.ListHistoryAsync();
            Assert.Equal(2, history.Count);
            Assert.Equal("RecentTool", history[0].ToolName);
            Assert.Equal("OldTool", history[1].ToolName);
            Assert.All(history, h => Assert.NotEqual(ApprovalStatus.Pending, h.Status));

            var limited = await store.ListHistoryAsync(limit: 1);
            Assert.Single(limited);
            Assert.Equal("RecentTool", limited[0].ToolName);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
        }
    }
}
