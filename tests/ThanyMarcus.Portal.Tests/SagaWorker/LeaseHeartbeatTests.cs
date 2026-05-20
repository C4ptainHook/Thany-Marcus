using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class LeaseHeartbeatTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Heartbeat_extends_lease_while_handler_is_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("hb-keys");
        var workspaceBase = MakeTempDir("hb-ws");
        try
        {
            var dp = new EphemeralDataProtectionProvider();
            var (_, _, job) = await SagaTestSeed.SeedAsync(
                Db, Clock, dp,
                status: SagaStatus.TfPlanning,
                ct: ct);
            Db.ChangeTracker.Clear();

            Directory.CreateDirectory(Path.Combine(workspaceBase, "stub"));

            var blocker = new BlockingPlanRunner();
            var cf = new FakeCloudflareDnsClient();

            using var host = SagaHostBuilder.Build(
                Postgres.ConnectionString, dpKeysDir, workspaceBase, blocker, cf,
                maxConcurrent: 1, dataProtectionProvider: dp, clock: Clock,
                extraConfig: cfg =>
                {
                    cfg["Provisioning:LeaseSeconds"] = "3";
                    cfg["Provisioning:HeartbeatSeconds"] = "1";
                    cfg["Provisioning:IdlePollMs"] = "200";
                });
            await host.StartAsync(ct);
            try
            {
                await WaitForAsync(
                    async () => await blocker.PlanCallCountAsync(ct) >= 1,
                    TimeSpan.FromSeconds(10), ct);

                var firstLease = await ReadLeaseAsync(job.Id, ct);
                firstLease.ShouldNotBeNull();

                await Task.Delay(TimeSpan.FromSeconds(2.5), ct);

                var secondLease = await ReadLeaseAsync(job.Id, ct);
                secondLease.ShouldNotBeNull();
                secondLease!.Value.ShouldBeGreaterThan(firstLease!.Value);

                var secondRow = await ReadRowAsync(job.Id, ct);
                secondRow.ClaimedBy.ShouldNotBeNull();

                blocker.ReleaseAll();
            }
            finally
            {
                blocker.ReleaseAll();
                await host.StopAsync(ct);
            }
        }
        finally
        {
            try { Directory.Delete(dpKeysDir, recursive: true); } catch { }
            try { Directory.Delete(workspaceBase, recursive: true); } catch { }
        }
    }

    private async Task<Instant?> ReadLeaseAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = NewDbContext();
        return await db.ProvisioningJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.LeaseExpiresAt)
            .SingleAsync(ct);
    }

    private async Task<ProvisioningJob> ReadRowAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = NewDbContext();
        return await db.ProvisioningJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
    }

    private PortalDbContext NewDbContext()
    {
        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PortalDbContext(opts);
    }

    private static string MakeTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("condition not met within " + timeout);
    }

    private sealed class BlockingPlanRunner : ITerraformRunner
    {
        private readonly TaskCompletionSource<TerraformResult> release = new();
        private int planCalls;

        public Task<int> PlanCallCountAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref planCalls));

        public void ReleaseAll() => release.TrySetResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> SelectOrCreateWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> SelectWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
        {
            Interlocked.Increment(ref planCalls);
            return release.Task.WaitAsync(ct);
        }

        public Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}"));

        public Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct) => Task.CompletedTask;

        public Task<TerraformResult> DeleteWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<bool> HasResourcesAsync(string workdir, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
