using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class DestroyEndToEndTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Destroying_job_walks_to_rolled_back_with_full_cleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("destroy-keys");
        var workspaceBase = MakeTempDir("destroy-ws");
        try
        {
            var dp = new EphemeralDataProtectionProvider();
            var (_, cloud, job) = await SagaTestSeed.SeedAsync(
                Db, Clock, dp,
                status: SagaStatus.Destroying,
                kind: SagaKinds.Destroy,
                seedCloudflareToken: true,
                ct: ct);

            var now = Clock.GetCurrentInstant();
            var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
            trackedCloud.Subdomain = $"sub-{Guid.NewGuid():N}".Substring(0, 16);
            trackedCloud.VmIp = "203.0.113.1";
            trackedCloud.TerraformWorkspace = cloud.Id.ToString();
            Db.PluginTokenMetadata.AddRange(
                new PluginTokenMetadata { CloudId = cloud.Id, Name = "t1", TokenHash = Guid.NewGuid().ToByteArray(), CreatedAt = now, UpdatedAt = now },
                new PluginTokenMetadata { CloudId = cloud.Id, Name = "t2", TokenHash = Guid.NewGuid().ToByteArray(), CreatedAt = now, UpdatedAt = now });

            var trackedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
            trackedJob.EventsLog.Dispose();
            trackedJob.EventsLog = JsonDocument.Parse("[{\"phase\":\"dns_creating\",\"record_id\":\"rec-existing-123\"}]");
            await Db.SaveChangesAsync(ct);
            Db.ChangeTracker.Clear();

            // Ensure a workdir exists so RollingBackTfHandler will run init+destroy.
            Directory.CreateDirectory(Path.Combine(workspaceBase, "jobs", job.Id.ToString()));

            var tf = new FakeTerraformRunner();
            tf.QueueInitOk();
            tf.QueueDestroyOk();
            var cf = new FakeCloudflareDnsClient();

            using var host = SagaHostBuilder.Build(Postgres.ConnectionString, dpKeysDir, workspaceBase, tf, cf);
            await host.StartAsync(ct);
            try
            {
                var reached = await WaitForStatusAsync(job.Id, SagaStatus.RolledBack, TimeSpan.FromSeconds(20), ct);
                reached.ShouldBeTrue();
            }
            finally
            {
                await host.StopAsync(ct);
            }

            var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
            reloadedCloud.DestroyedAt.ShouldNotBeNull();
            reloadedCloud.VmIp.ShouldBeNull();
            reloadedCloud.TerraformWorkspace.ShouldBeNull();

            var tokens = await Db.PluginTokenMetadata.Where(p => p.CloudId == cloud.Id).ToListAsync(ct);
            tokens.Count.ShouldBe(2);
            tokens.ShouldAllBe(p => p.RevokedAt != null);

            cf.Deletes.ShouldContain("rec-existing-123");
            tf.DeleteWorkspaceCalls.ShouldHaveSingleItem();
            tf.DeleteWorkspaceCalls[0].ShouldBe(cloud.Id.ToString());
        }
        finally
        {
            try { Directory.Delete(dpKeysDir, recursive: true); } catch { }
            try { Directory.Delete(workspaceBase, recursive: true); } catch { }
        }
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
