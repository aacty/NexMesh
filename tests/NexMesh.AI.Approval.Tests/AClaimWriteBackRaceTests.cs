namespace NexMesh.AI.Approval.Tests;

using FreeSql;
using NexMesh.AI.Approval;

/// <summary>
/// 攻击面 B(框架架构师 A 对抗性交叉测试):认领后写回竞态。
/// 【已修复并反转】原报告实证 UpdateAsync 无状态守卫(陈旧快照可复活已认领/已终态单 → 二次认领);
/// 修复 = 双 store 终态守卫(InMemory 读守卫 / FreeSql WHERE 排除终态行,影响 0 行判负)。
/// 本测试现锁定修复语义:陈旧写回被 409 拒绝,状态不被复活,二次认领不得手。
/// </summary>
public class AClaimWriteBackRaceTests
{
    [Fact]
    public async Task 内存实现_陈旧快照写回_被终态守卫拒绝_不复活()
    {
        var store = new InMemoryApprovalStore();

        var request = new ApprovalRequest { ToolName = "高危:探针", IdempotencyKey = "k-writeback" };
        await store.AddAsync(request);

        // 陈旧快照:旁路(审计/看板/默认接口实现)在认领前读到的 Pending 对象
        var stale = await store.GetAsync(request.Id);
        Assert.NotNull(stale);

        // 管理员 A 原子认领成功(Pending → Approved)
        var claimed = await store.ClaimForDecisionAsync(request.Id, ApprovalStatus.Approved, decidedBy: "admin-a");
        Assert.NotNull(claimed);

        // 攻击:陈旧 Pending 快照写回 → 终态守卫拒绝(409)
        var ex = await Assert.ThrowsAsync<NexMesh.MeshException>(() => store.UpdateAsync(stale!));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(ApprovalStatus.Approved, (await store.GetAsync(request.Id))!.Status); // 未被复活

        // 复活不可得 → 二次认领必须失败(单据仍为 Approved,非 Pending)
        var secondClaim = await store.ClaimForDecisionAsync(request.Id, ApprovalStatus.Approved, decidedBy: "admin-b");
        Assert.Null(secondClaim);

        // 终态亦受守卫:执行完成后陈旧写回同样被拒
        var executed = claimed with { Status = ApprovalStatus.Executed, Result = "done" };
        await store.UpdateAsync(executed);
        var ex2 = await Assert.ThrowsAsync<NexMesh.MeshException>(() => store.UpdateAsync(stale!));
        Assert.Equal(409, ex2.StatusCode);
        Assert.Equal(ApprovalStatus.Executed, (await store.GetAsync(request.Id))!.Status);
        Assert.Null(await store.ClaimForDecisionAsync(request.Id, ApprovalStatus.Approved, decidedBy: "admin-c"));
    }

    [Fact]
    public async Task 真库实现_陈旧快照写回_被终态守卫拒绝_不复活()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-writeback-{Guid.CreateVersion7():N}.db");
        try
        {
            var db = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
                .UseAutoSyncStructure(true)
                .Build();
            var store = new FreeSqlApprovalStore(db);

            var request = new ApprovalRequest { ToolName = "高危:探针", IdempotencyKey = "k-writeback-db" };
            await store.AddAsync(request);

            var stale = await store.GetAsync(request.Id);
            Assert.NotNull(stale);

            var claimed = await store.ClaimForDecisionAsync(request.Id, ApprovalStatus.Approved, decidedBy: "admin-a");
            Assert.NotNull(claimed);

            // 攻击:陈旧 Pending 快照写回(Approved 阶段)→ 状态机单调守卫拒绝(禁向后写回)
            var ex = await Assert.ThrowsAsync<NexMesh.MeshException>(() => store.UpdateAsync(stale!));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(ApprovalStatus.Approved, (await store.GetAsync(request.Id))!.Status); // 未被复活

            // 复活不可得 → 二次认领必须失败
            Assert.Null(await store.ClaimForDecisionAsync(request.Id, ApprovalStatus.Approved, decidedBy: "admin-b"));

            // 终态守卫:执行完成后陈旧写回同样被拒
            await store.UpdateAsync(claimed with { Status = ApprovalStatus.Executed, Result = "done" });
            var ex2 = await Assert.ThrowsAsync<NexMesh.MeshException>(() => store.UpdateAsync(stale!));
            Assert.Equal(409, ex2.StatusCode);
            Assert.Equal(ApprovalStatus.Executed, (await store.GetAsync(request.Id))!.Status);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理失败不掩盖测试结果 */ }
        }
    }
}
