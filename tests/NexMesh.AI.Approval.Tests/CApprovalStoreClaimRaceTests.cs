namespace NexMesh.AI.Approval.Tests;

using FreeSql;

/// <summary>
/// 攻击者视角(C,数据层交叉测试):下沉到 store 级直打 FreeSqlApprovalStore.ClaimForDecisionAsync——
/// 服务级测试(ApprovalRaceTests)经决策服务+工具执行,胜者还会执行工具与二次 Update;
/// 本文件剥离服务层,直接对"同一 Pending 单 16 并发混合认领"做 store 级不变量实证:
///   ① 恰好一人胜出(返回非 null),其余一律 null,无异常逃逸;
///   ② 库中终态唯一且与胜者盖章一致(Status/DecidedBy),绝不出现混合双写;
///   ③ 终态后重放认领判负、终态不翻转(Rejected 不得覆盖 Approved)。
/// 真库用 SQLite 文件库(%TEMP% 唯一名,finally 删除)——共享缓存内存库在连接池下每连接独立,竞态证据无效。
/// </summary>
public class CApprovalStoreClaimRaceTests
{
    private static (IFreeSql Db, string Path) NewFileDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexmesh-c-claim-{Guid.CreateVersion7():N}.db");
        var db = new FreeSqlBuilder()
            .UseConnectionString(DataType.Sqlite, $"Data Source={path}")
            .Build(); // 不开 AutoSyncStructure:证明 store 自身 AsyncLazy 建表自洽
        return (db, path);
    }

    private static void DeleteDbQuietly(string path)
    {
        // 文件锁(连接未释放完)按约定等 30 秒;清理失败不掩盖测试结果
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
        }
    }

    [Fact]
    public async Task 同一待审单16并发混合认领_恰好一人胜出_库中终态唯一且与胜者一致()
    {
        var (db, dbPath) = NewFileDb();
        try
        {
            var store = new FreeSqlApprovalStore(db);
            var pending = new ApprovalRequest
            {
                ToolName = "delete-patient",
                Description = "store 级竞态探针",
                ArgumentsJson = """{"id":1}""",
                IdempotencyKey = "war-1",
            };
            await store.AddAsync(pending);

            const int N = 16;
            var outcomes = new (ApprovalRequest? Claim, Exception? Error)[N];

            // 闸后齐发:8 批准 + 8 拒绝,同一单同一瞬间
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var war = Parallel.ForAsync(0, N, async (i, _) =>
            {
                var decision = i % 2 == 0 ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
                try
                {
                    await gate.Task;
                    outcomes[i] = (await store.ClaimForDecisionAsync(pending.Id, decision, $"admin-{i}"), null);
                }
                catch (Exception ex)
                {
                    outcomes[i] = (null, ex);
                }
            });
            gate.TrySetResult(); // 16 个任务已全部挂闸,齐放
            await war;

            var errors = outcomes.Where(o => o.Error is not null).ToList();
            Assert.True(errors.Count is 0,
                "认领路径不得有异常逃逸(SQLITE_BUSY 等应被 WHERE 守卫吸收或明确判负):"
                + string.Join(" | ", errors.Select(e => $"{e.Error!.GetType().Name}:{e.Error.Message}")));

            var winners = outcomes.Where(o => o.Claim is not null).ToList();
            Assert.True(winners.Count is 1,
                $"恰好一人胜出,实际 {winners.Count} 人非 null(双执行=高危不变量被击穿)");
            var winnerIndex = Array.FindIndex(outcomes, o => o.Claim is not null);
            var winner = outcomes[winnerIndex].Claim!;
            Assert.True(winner.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected,
                $"胜者状态非法:{winner.Status}");
            Assert.NotNull(winner.DecidedAt);
            Assert.Equal($"admin-{winnerIndex}", winner.DecidedBy);

            // 库中终态唯一:单行、状态与胜者一致、DecidedBy 与胜者一致(无混合双写)
            var rows = db.Select<ApprovalRequestEntity>().ToList();
            var final = Assert.Single(rows);
            Assert.Equal(winner.Status, final.Status);
            Assert.Equal(winner.DecidedBy, final.DecidedBy);
            Assert.NotEqual(ApprovalStatus.Pending, final.Status);

            // 服务层视角再读一遍:与直接查库一致
            var viaStore = await store.GetAsync(pending.Id);
            Assert.Equal(final.Status, viaStore!.Status);
        }
        finally
        {
            (db as IDisposable)?.Dispose();
            DeleteDbQuietly(dbPath);
        }
    }

    [Fact]
    public async Task 终态后重放认领与非存在ID_一律判负_终态不翻转()
    {
        var (db, dbPath) = NewFileDb();
        try
        {
            var store = new FreeSqlApprovalStore(db);
            var pending = new ApprovalRequest { ToolName = "t", IdempotencyKey = "war-2" };
            await store.AddAsync(pending);

            // 非存在 ID:必须 null(不抛、不静默创建)
            Assert.Null(await store.ClaimForDecisionAsync("no-such-id", ApprovalStatus.Approved, "admin-x"));

            // 首次认领:批准胜出
            var claimed = await store.ClaimForDecisionAsync(pending.Id, ApprovalStatus.Approved, "admin-first");
            Assert.NotNull(claimed);
            Assert.Equal(ApprovalStatus.Approved, claimed!.Status);

            // 终态后重放:拒绝重放、批准重放,一律 null 且终态不翻转(Rejected 不得覆盖 Approved)
            Assert.Null(await store.ClaimForDecisionAsync(pending.Id, ApprovalStatus.Rejected, "admin-attacker"));
            Assert.Null(await store.ClaimForDecisionAsync(pending.Id, ApprovalStatus.Approved, "admin-again"));

            var final = await store.GetAsync(pending.Id);
            Assert.Equal(ApprovalStatus.Approved, final!.Status);
            Assert.Equal("admin-first", final.DecidedBy);

            // 终态进入历史、退出 Pending 队列
            Assert.Empty(await store.ListPendingAsync());
            var history = await store.ListHistoryAsync(10);
            Assert.Single(history);
            Assert.Equal(pending.Id, history[0].Id);
        }
        finally
        {
            (db as IDisposable)?.Dispose();
            DeleteDbQuietly(dbPath);
        }
    }
}
