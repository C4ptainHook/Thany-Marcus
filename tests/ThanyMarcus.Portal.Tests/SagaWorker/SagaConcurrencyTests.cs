using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class SagaConcurrencyTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Five_pending_jobs_yield_exactly_three_concurrent_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("conc-keys");
        var workspaceBase = MakeTempDir("conc-ws");
        try
        {
            var dp = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var jobs = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var (_, _, job) = await SagaTestSeed.SeedAsync(
                    Db, Clock, dp,
                    status: SagaStatus.TfPlanning,
                    ct: ct);
                jobs.Add(job.Id);
                Db.ChangeTracker.Clear();
            }

            Directory.CreateDirectory(Path.Combine(workspaceBase, "stub"));

            var blocker = new BlockingPlanRunner();
            var cf = new FakeCloudflareDnsClient();

            using var host = SagaHostBuilder.Build(
                Postgres.ConnectionString, dpKeysDir, workspaceBase, blocker, cf,
                maxConcurrent: 3, dataProtectionProvider: dp,
                clock: Clock);
            await host.StartAsync(ct);
            try
            {
                await WaitForAsync(async () => await blocker.PlanCallCountAsync(ct) == 3, TimeSpan.FromSeconds(15), ct);

                var claimedCount = await CountClaimedAsync(jobs, ct);
                claimedCount.ShouldBe(3);

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

    private async Task<int> CountClaimedAsync(List<Guid> jobIds, CancellationToken ct)
    {
        using var probeDb = NewDbContext();
        return await probeDb.ProvisioningJobs
            .Where(j => jobIds.Contains(j.Id) && j.ClaimedBy != null)
            .CountAsync(ct);
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

    private sealed class BlockingPlanRunner : ITerraformRunner
    {
        private readonly TaskCompletionSource<TerraformResult> release = new();
        private int planCalls;

        public Task<int> PlanCallCountAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref planCalls));

        public void ReleaseAll() => release.TrySetResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct) =>
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
    }
}
