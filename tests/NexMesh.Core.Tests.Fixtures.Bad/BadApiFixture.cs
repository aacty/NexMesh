namespace NexMesh.Core.Fixtures.Bad;

using NexMesh.Core.AutoApi;

/// <summary>多复杂参数夹具(独立程序集):映射期应 fail-fast 阻断(Phase 1 有界绑定不支持)。</summary>
public record DtoA(string A);

[ServiceRoute("api/bad")]
public class BadApiService : IAutoApi
{
    public Task<int> TwoBodiesAsync(DtoA a, DtoA b) => Task.FromResult(1);
}
