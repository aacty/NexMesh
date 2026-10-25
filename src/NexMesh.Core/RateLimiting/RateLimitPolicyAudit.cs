namespace NexMesh.Core.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// 启动期限流策略审计:遍历已映射端点的 RateLimiterMetadata,引用了未注册策略名的记 Warning
/// (运行期该端点首次请求会 500——启动期可见化,避免上线才发现拼写错误)。
/// </summary>
public static class RateLimitPolicyAudit
{
    /// <summary>RateLimiterMetadata 在 .NET 10 起未公开导出,经反射按名读取 PolicyName。</summary>
    private static readonly PropertyInfo? PolicyNameProperty = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("Microsoft.AspNetCore.RateLimiting.RateLimiterMetadata", throwOnError: false))
        .OfType<Type>()
        .FirstOrDefault()?
        .GetProperty("PolicyName", BindingFlags.Public | BindingFlags.Instance);

    public static void AuditPolicies(WebApplication app)
    {
        if (PolicyNameProperty is null)
        {
            return; // 类型结构变化时静默跳过审计(不影响功能)
        }
        var marker = app.Services.GetRequiredService<NexMeshRateLimitMarker>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            var metadata = endpoint.Metadata.FirstOrDefault(m =>
                string.Equals(m.GetType().Name, "RateLimiterMetadata", StringComparison.Ordinal));
            if (metadata is not null
                && PolicyNameProperty.GetValue(metadata) is string policy
                && !marker.RegisteredPolicies.Contains(policy))
            {
                unknown.Add(policy);
            }
        }
        if (unknown.Count > 0)
        {
            app.Logger.LogWarning(
                "[NexMesh.RateLimit] 端点引用了未注册的限流策略:{Policies}(未注册策略的端点运行期将 500;" +
                "请检查 NexMesh:RateLimit:Policies 配置或 configure 回调注册{Note})",
                string.Join(",", unknown),
                marker.HasCustomConfiguration ? "——已提供 configure 回调,若策略在回调中注册可忽略" : string.Empty);
        }
    }
}
