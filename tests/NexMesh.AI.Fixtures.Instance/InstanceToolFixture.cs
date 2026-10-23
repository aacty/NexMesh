namespace NexMesh.AiFixtures.Instance;

using NexMesh.AI.Tools;

/// <summary>实例工具宿主的 Scoped 依赖(用于验证每次调用独立作用域解析)。</summary>
public interface IProbeDependency
{
    Guid InstanceId { get; }
}

public sealed class ProbeDependency : IProbeDependency
{
    public Guid InstanceId { get; } = Guid.NewGuid();
}

/// <summary>
/// 实例方法工具夹具(独立程序集):构造注入依赖,返回依赖实例 Id——
/// 两次调用返回不同 Id 即证明每次调用都在独立 DI 作用域内解析(无 Scoped/Singleton 纠缠)。
/// </summary>
public class InstanceToolFixture(IProbeDependency dependency)
{
    [NexMeshTool("实例方法工具:返回其 Scoped 依赖实例Id,验证 DI 生命周期托管", NexMeshRiskLevel.Low)]
    public Task<string> ProbeAsync()
        => Task.FromResult(dependency.InstanceId.ToString());

    [NexMeshTool("按生效日期安排巡房计划(强类型参数,供自纠错测试)", NexMeshRiskLevel.Low)]
    public Task<string> ScheduleRoundsAsync(DateTime effectiveDate)
        => Task.FromResult($"巡房计划已安排:{effectiveDate:yyyy-MM-dd}");
}
