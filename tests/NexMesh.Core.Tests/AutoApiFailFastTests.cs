namespace NexMesh.Core.Tests;

using Microsoft.AspNetCore.Builder;
using NexMesh;
using NexMesh.Core.Fixtures.Bad;

public class ContractFailFastTests
{
    [Fact]
    public void 多个复杂类型参数_映射期阻断启动()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var ex = Assert.Throws<NotSupportedException>(
            () => app.MapNexMeshContracts(typeof(BadApiService).Assembly));
        Assert.Contains("JSON Body 复杂类型参数", ex.Message);
    }
}
