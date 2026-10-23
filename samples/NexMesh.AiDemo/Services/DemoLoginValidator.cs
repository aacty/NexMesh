namespace NexMesh.AiDemo.Services;

using NexMesh.Auth;
using NexMesh.Core.DependencyInjection;

/// <summary>演示登录验证(固定账号 doctor/123456;真实业务查用户表+密码哈希)。</summary>
public class DemoLoginValidator : ILoginValidator, ITransientService
{
    public Task<LoginUser?> ValidateAsync(string username, string password)
    {
        LoginUser? user = username switch
        {
            "doctor" when password == "123456" => new("doc-001", "张三医生", "hosp-A",
                new Dictionary<string, string> { ["role"] = "doctor" }),
            "admin" when password == "admin123" => new("admin-001", "管理员", "hosp-A",
                new Dictionary<string, string> { ["role"] = "admin" }),
            _ => null,
        };
        return Task.FromResult(user);
    }
}
