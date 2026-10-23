namespace NexMesh.AI.Tests.Fixtures;

using NexMesh.AI.Tools;

/// <summary>低风险静态工具夹具(12 个,用于发现/截断/调用测试)。注意:本程序集禁止出现 High/实例工具——它们在独立夹具程序集中。</summary>
public static class LowTools
{
    [NexMeshTool("演示工具01", NexMeshRiskLevel.Low)]
    public static Task<string> Tool01Async() => Task.FromResult("01");

    [NexMeshTool("演示工具02", NexMeshRiskLevel.Low)]
    public static Task<string> Tool02Async() => Task.FromResult("02");

    [NexMeshTool("演示工具03", NexMeshRiskLevel.Low)]
    public static Task<string> Tool03Async() => Task.FromResult("03");

    [NexMeshTool("演示工具04", NexMeshRiskLevel.Low)]
    public static Task<string> Tool04Async() => Task.FromResult("04");

    [NexMeshTool("演示工具05", NexMeshRiskLevel.Low)]
    public static Task<string> Tool05Async() => Task.FromResult("05");

    [NexMeshTool("演示工具06", NexMeshRiskLevel.Low)]
    public static Task<string> Tool06Async() => Task.FromResult("06");

    [NexMeshTool("演示工具07", NexMeshRiskLevel.Low)]
    public static Task<string> Tool07Async() => Task.FromResult("07");

    [NexMeshTool("演示工具08", NexMeshRiskLevel.Low)]
    public static Task<string> Tool08Async() => Task.FromResult("08");

    [NexMeshTool("演示工具09", NexMeshRiskLevel.Low)]
    public static Task<string> Tool09Async() => Task.FromResult("09");

    [NexMeshTool("演示工具10", NexMeshRiskLevel.Low)]
    public static Task<string> Tool10Async() => Task.FromResult("10");

    [NexMeshTool("演示工具11", NexMeshRiskLevel.Low)]
    public static Task<string> Tool11Async() => Task.FromResult("11");

    [NexMeshTool("回显工具:原样返回输入文本", NexMeshRiskLevel.Low)]
    public static Task<string> EchoAsync(string text) => Task.FromResult(text);

    [NexMeshTool("批量调整库存(生效日期+数量)", NexMeshRiskLevel.Low)]
    public static Task<string> RestockAsync(DateTime effectiveDate, int quantity)
        => Task.FromResult($"已按 {effectiveDate:yyyy-MM-dd} 调整 {quantity} 件");
}
