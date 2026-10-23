namespace NexMesh.Host.Tests;

using FreeSql;
using NexMesh.AI.Tools;
using NexMesh.Core.AutoApi;
using NexMesh.Core.DependencyInjection;

/// <summary>宿主端到端夹具:业务开发者视角的全部产出——只有业务类 + 工具方法,零框架装配代码。</summary>
[ServiceRoute("api/host-demo")]
public class HostDemoService : IAutoApi, IScopedService
{
    private readonly IFreeSql _db;

    public HostDemoService(IFreeSql db) => _db = db; // IFreeSql 由宿主注入,业务只管用

    public Task<string> PingAsync() => Task.FromResult("host-ok");

    public async Task<long> CountAsync()
    {
        await _db.Insert(new HostRow { Value = "x" }).ExecuteAffrowsAsync();
        return await _db.Select<HostRow>().CountAsync();
    }
}

public class HostRow
{
    [FreeSql.DataAnnotations.Column(IsIdentity = true)]
    public long Id { get; set; }
    public string Value { get; set; } = "";
}

public static class HostDemoTools
{
    [NexMeshTool("宿主演示工具(低风险只读)", NexMeshRiskLevel.Low)]
    public static Task<string> ProbeAsync() => Task.FromResult("tool-ok");
}
