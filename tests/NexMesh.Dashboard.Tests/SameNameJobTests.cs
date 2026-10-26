using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI.Approval;
using NexMesh.Auth;
using NexMesh.Host;
using NexMesh.Worker;
using Xunit;

namespace NexMesh.Dashboard.Tests.NsA
{
    public class TwinJob(string cron, bool runOnStart, IServiceProvider sp)
        : NexMesh.Worker.NexMeshCronJob(cron, runOnStart, provider: sp, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct) => Task.CompletedTask;
    }
}
namespace NexMesh.Dashboard.Tests.NsB
{
    public class TwinJob(string cron, bool runOnStart, IServiceProvider sp)
        : NexMesh.Worker.NexMeshCronJob(cron, runOnStart, provider: sp, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct) => Task.CompletedTask;
    }
}

namespace NexMesh.Dashboard.Tests
{
    public class SameNameJobTests
    {
        public sealed class TwinValidator : ILoginValidator
        {
            public Task<LoginUser?> ValidateAsync(string username, string password)
                => Task.FromResult<LoginUser?>(username == "demo" && password == "demo123" ? new("twin-1", "双作业用户") : null);
        }

        [Fact]
        public async Task 同名作业_按JobName触发报歧义_按类型全名触发成功()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
            builder.Configuration["NexMesh:Db:ConnectionString"] = $"Data Source=twin-{Guid.NewGuid():N}.db";
            builder.Configuration["NexMesh:Db:AutoSyncStructure"] = "true";
            builder.Configuration["NexMesh:AI:Approval:Enabled"] = "true";
            builder.WebHost.UseTestServer();
            builder.AddNexMeshService(typeof(SameNameJobTests).Assembly);
            builder.Services.AddNexMeshAuth(builder.Configuration);
            builder.Services.AddNexMeshApproval(new ConfigurationBuilder().Build());
            builder.Services.AddTransient<ILoginValidator, TwinValidator>();
            builder.Services.AddNexMeshCronJob<NsA.TwinJob>("0 2 * * *");
            builder.Services.AddNexMeshCronJob<NsB.TwinJob>("0 3 * * *");
            var app = builder.Build();
            app.UseNexMeshService(typeof(SameNameJobTests).Assembly);
            app.MapNexMeshAuth();
            app.MapNexMeshDashboard(force: true);
            await app.StartAsync();
            var client = app.GetTestClient();
            var login = await client.PostAsync("/nexmesh/auth/login",
                new StringContent(JsonSerializer.Serialize(new { username = "demo", password = "demo123" }), Encoding.UTF8, "application/json"));
            var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var ambiguous = await client.PostAsync("/nexmesh/dashboard/jobs/TwinJob/trigger", content: null);
            var ambiguousBody = await ambiguous.Content.ReadAsStringAsync();
            Assert.Contains("同名作业 2 个", ambiguousBody);

            var precise = await client.PostAsync("/nexmesh/dashboard/jobs/" + Uri.EscapeDataString(typeof(NsA.TwinJob).FullName!) + "/trigger", content: null);
            var preciseBody = JsonDocument.Parse(await precise.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            Assert.True(preciseBody.GetProperty("triggered").GetBoolean());
            await app.DisposeAsync();
        }
    }
}
