using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Auth;
using NexMesh.Core;
using NexMesh.Core.DependencyInjection;
using Xunit;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using GrpcChannelOptions = Grpc.Net.Client.GrpcChannelOptions;

namespace NexMesh.Host.Tests;

/// <summary>
/// 攻击面 1(框架架构师 B 对抗测试):契约授权在 REST/gRPC 双面的行为一致性。
/// 【已修复并反转】原报告实证 gRPC 面零授权检查(REST 默认拒绝被 gRPC 匿名直通整体绕过,
/// 角色门同样失效);修复 = ContractGatewayService.Invoke 镜像 REST 虚拟网关同一判定
/// (默认拒绝/[Authorize]/[Authorize(Roles)],未认证→Unauthenticated,角色不足→PermissionDenied)。
/// 本测试现锁定双面一致:REST 401/403 的契约,gRPC 同样拒绝;仅 [AllowAnonymous] 双面放行。
/// </summary>
public class BGrpcAuthBypassTests : IAsyncLifetime
{
    /// <summary>受保护契约:三种授权形态(全部不带 AllowAnonymous)。</summary>
    public interface ISecretVaultApi : IServiceApi
    {
        /// <summary>无任何标注 → REST 默认拒绝(AllowAnonymousByDefault=false)。</summary>
        [Microsoft.AspNetCore.Mvc.HttpGet]
        Task<string> GetBalanceAsync();

        /// <summary>显式 [Authorize] → REST 要求已认证。</summary>
        [Authorize]
        [Microsoft.AspNetCore.Mvc.HttpGet]
        Task<string> GetProfileAsync();

        /// <summary>显式 [Authorize(Roles="Admin")] → REST 要求 Admin 角色。</summary>
        [Authorize(Roles = "Admin")]
        [Microsoft.AspNetCore.Mvc.HttpGet]
        Task<string> GetAuditLogAsync();

        /// <summary>对照组:AllowAnonymous 两面均应放行(证明测试装置本身有效)。</summary>
        [AllowAnonymous]
        [Microsoft.AspNetCore.Mvc.HttpGet]
        Task<string> PingAsync();
    }

    public class SecretVaultService : ISecretVaultApi, IScopedService
    {
        public Task<string> GetBalanceAsync() => Task.FromResult("vault-balance-999999");
        public Task<string> GetProfileAsync() => Task.FromResult("profile-secret-of-alice");
        public Task<string> GetAuditLogAsync() => Task.FromResult("audit-log-top-secret");
        public Task<string> PingAsync() => Task.FromResult("pong");
    }

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // 注意:不设置 AllowAnonymousByDefault → REST 面默认拒绝生效(与生产一致)
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshContracts(typeof(SecretVaultService).Assembly);
        builder.Services.AddNexMeshGrpc(typeof(SecretVaultService).Assembly);
        builder.Services.AddNexMeshAuth(builder.Configuration); // 认证/授权管线与生产同构
        _app = builder.Build();
        _app.UseNexMeshAuthentication(); // 认证中间件前置(gRPC 请求同样经过)
        _app.UseAuthorization();
        _app.MapNexMeshContracts(typeof(SecretVaultService).Assembly);
        _app.MapNexMeshGrpc();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private GrpcChannel CreateChannel()
        => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = ((TestServer)_app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!).CreateHandler(),
        });

    private NexMesh.Grpc.ContractGateway.ContractGatewayClient Grpc() =>
        new(CreateChannel());

    private static async Task<string> RestGetAsync(HttpClient client, string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }
        var response = await client.SendAsync(request);
        return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
    }

    private static async Task<string> GrpcInvokeAsync(NexMesh.Grpc.ContractGateway.ContractGatewayClient client,
        string service, string method, string argsJson = "{}")
    {
        var reply = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
        {
            Service = service,
            Method = method,
            ArgsJson = argsJson,
        });
        return reply.EnvelopeJson;
    }

    [Fact]
    public async Task 对照组_AllowAnonymous契约_REST与gRPC匿名均200()
    {
        // 装置有效性:放行契约在两面行为一致 —— 后续拒绝断言不是装置坏了
        var rest = await RestGetAsync(_http, "/api/SecretVault/Ping");
        Assert.StartsWith("200|", rest);
        Assert.Contains("pong", rest);

        var grpc = await GrpcInvokeAsync(Grpc(), "secretvault", "Ping");
        Assert.Contains("pong", grpc);
    }

    [Fact]
    public async Task 双面一致_默认拒绝契约_REST与gRPC匿名均拒绝()
    {
        // REST 面:默认拒绝 → 401 信封
        var rest = await RestGetAsync(_http, "/api/SecretVault/GetBalance");
        Assert.StartsWith("401|", rest);
        Assert.Contains("\"code\":401", rest.Replace(" ", ""));

        // gRPC 面(修复后):同一契约方法,零凭据 → Unauthenticated(镜像 REST 判定)
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await Grpc().InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetBalance", ArgsJson = "{}" }));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task 双面一致_显式Authorize契约_两面匿名均拒绝()
    {
        var rest = await RestGetAsync(_http, "/api/SecretVault/GetProfile");
        Assert.StartsWith("401|", rest);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await Grpc().InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetProfile", ArgsJson = "{}" }));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task 双面一致_角色契约_低权与匿名在两面均拒绝()
    {
        // 合法低权令牌(ClaimTypes.Role=Staff):REST 面角色不足 → 403
        var tokens = _app.Services.GetRequiredService<NexMeshTokenService>();
        var staffSession = await tokens.IssueAsync(new LoginUser("staff-1", "普通员工", null, null)
        {
            ClaimList = [new TokenClaim(ClaimTypes.Role, "Staff")],
        });
        var restWithStaff = await RestGetAsync(_http, "/api/SecretVault/GetAuditLog", staffSession.Token);
        Assert.StartsWith("403|", restWithStaff);

        // REST 匿名 → 401
        var restAnonymous = await RestGetAsync(_http, "/api/SecretVault/GetAuditLog");
        Assert.StartsWith("401|", restAnonymous);

        // gRPC(修复后):Staff 低权令牌 → PermissionDenied(镜像 REST 403)
        var deniedEx = await Assert.ThrowsAsync<RpcException>(async () =>
            await Grpc().InvokeAsync(
                new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetAuditLog", ArgsJson = "{}" },
                headers: new Metadata { { "authorization", $"Bearer {staffSession.Token}" } }));
        Assert.Equal(StatusCode.PermissionDenied, deniedEx.StatusCode);

        // gRPC 匿名 → Unauthenticated(镜像 REST 401)
        var anonEx = await Assert.ThrowsAsync<RpcException>(async () =>
            await Grpc().InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetAuditLog", ArgsJson = "{}" }));
        Assert.Equal(StatusCode.Unauthenticated, anonEx.StatusCode);
    }

    [Fact]
    public async Task gRPC_携带无效Bearer_仍被拒绝闸_Unauthenticated()
    {
        // 无效 Bearer:RejectInvalidBearerMiddleware 对 gRPC 请求同样生效(401→Unauthenticated)
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await Grpc().InvokeAsync(
                new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetBalance", ArgsJson = "{}" },
                headers: new Metadata { { "authorization", "Bearer totally-bogus-token" } }));
        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    [Fact]
    public async Task gRPC_Admin令牌_角色契约放行_双面一致()
    {
        // 修复后补:Admin 角色经 gRPC 调 Admin 契约 → 放行(授权不止会拒,也会正确放行)
        var tokens = _app.Services.GetRequiredService<NexMeshTokenService>();
        var adminSession = await tokens.IssueAsync(new LoginUser("admin-9", "管理员", null, null)
        {
            ClaimList = [new TokenClaim(ClaimTypes.Role, "Admin")],
        });

        var reply = await Grpc().InvokeAsync(
            new NexMesh.Grpc.ContractCall { Service = "secretvault", Method = "GetAuditLog", ArgsJson = "{}" },
            headers: new Metadata { { "authorization", $"Bearer {adminSession.Token}" } });
        Assert.Contains("\"success\":true", reply.EnvelopeJson.Replace(" ", ""));
        Assert.Contains("audit-log-top-secret", reply.EnvelopeJson);
    }
}
