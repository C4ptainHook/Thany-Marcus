using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class SagaEndToEndTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Pending_job_walks_to_awaiting_cloud_callback()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("e2e-keys");
        var workspaceBase = MakeTempDir("e2e-ws");
        try
        {
            var dp = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var (_, _, job) = await SagaTestSeed.SeedAsync(
                Db, Clock, dp,
                status: SagaStatus.Pending,
                seedCloudflareToken: true,
                ct: ct);
            Db.ChangeTracker.Clear();

            var tf = new FakeTerraformRunner();
            tf.QueueInitOk(); tf.QueuePlanOk();
            tf.QueueApplyOk(); tf.QueueOutputJson("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}");
            var cf = new FakeCloudflareDnsClient();

            await TransitionPendingToTfPlanningAsync(job.Id, ct);

            Directory.CreateDirectory(Path.Combine(workspaceBase, "stub"));
            using var host = SagaHostBuilder.Build(
                Postgres.ConnectionString, dpKeysDir, workspaceBase, tf, cf,
                dataProtectionProvider: dp,
                clock: Clock);
            await host.StartAsync(ct);
            try
            {
                var reached = await WaitForStatusAsync(job.Id, SagaStatus.AwaitingCloudCallback, TimeSpan.FromSeconds(20), ct);
                reached.ShouldBeTrue();
            }
            finally
            {
                await host.StopAsync(ct);
            }
        }
        finally
        {
            try { Directory.Delete(dpKeysDir, recursive: true); } catch { }
            try { Directory.Delete(workspaceBase, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Awaiting_cert_when_polled_with_cert_ready_reaches_succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("e2e2-keys");
        var workspaceBase = MakeTempDir("e2e2-ws");
        try
        {
            var dp = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var (_, _, job) = await SagaTestSeed.SeedAsync(
                Db, Clock, dp,
                status: SagaStatus.AwaitingCert,
                ct: ct);

            var trackedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
            var protector = dp.CreateProtector("cloud-admin-token:v1");
            trackedJob.AdminTokenCiphertext =
                protector.Protect(System.Text.Encoding.UTF8.GetBytes("stub-admin-token"));
            await Db.SaveChangesAsync(ct);
            Db.ChangeTracker.Clear();

            var tf = new FakeTerraformRunner();
            var cf = new FakeCloudflareDnsClient();

            using var host = SagaHostBuilder.Build(
                Postgres.ConnectionString, dpKeysDir, workspaceBase, tf, cf,
                certPollReady: true, dataProtectionProvider: dp,
                clock: Clock);
            await host.StartAsync(ct);
            try
            {
                var reached = await WaitForStatusAsync(job.Id, SagaStatus.Succeeded, TimeSpan.FromSeconds(10), ct);
                reached.ShouldBeTrue();
            }
            finally
            {
                await host.StopAsync(ct);
            }
        }
        finally
        {
            try { Directory.Delete(dpKeysDir, recursive: true); } catch { }
            try { Directory.Delete(workspaceBase, recursive: true); } catch { }
        }
    }

    private async Task TransitionPendingToTfPlanningAsync(Guid jobId, CancellationToken ct)
    {
        var pending = await Db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        pending.Status = SagaStatus.TfPlanning;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
    }

    private async Task<bool> WaitForStatusAsync(Guid jobId, string targetStatus, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            using var probeDb = NewDbContext();
            var status = await probeDb.ProvisioningJobs
                .Where(j => j.Id == jobId)
                .Select(j => j.Status)
                .SingleAsync(ct);
            if (status == targetStatus) return true;
            await Task.Delay(200, ct);
        }
        return false;
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
}
