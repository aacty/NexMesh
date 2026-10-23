namespace NexMesh.Core.Fixtures.Bad;

using NexMesh;

/// <summary>多复杂参数夹具(独立程序集):映射期应 fail-fast 阻断(有界绑定不支持多 Body)。</summary>
public record DtoA(string A);

public interface IBadApi : IServiceApi
{
    Task<int> TwoBodiesAsync(DtoA a, DtoA b);
}

public class BadApiService : IBadApi
{
    public Task<int> TwoBodiesAsync(DtoA a, DtoA b) => Task.FromResult(1);
}
