using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class BlueGreenHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal)
    {
        SagaStatus.MigrateSucceeded, SagaStatus.FailedMigrate, SagaStatus.MigrateRolledBack,
    };

    [Fact]
    public async Task Happy_path_walks_quiesce_snapshot_provision_verify_cutover_gate_destroy_to_succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = new RecordingOps();
        var (cloud, finalStatus) = await DriveAsync(ops, ct);

        finalStatus.ShouldBe(SagaStatus.MigrateSucceeded);

        Db.ChangeTracker.Clear();
        var reloaded = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloaded.ProvisioningStatus.ShouldBe(SagaStatus.Succeeded);

        // Load-bearing ordering: the volume snapshot must happen AFTER quiesce.
        ops.Calls.IndexOf("find_old").ShouldBeLessThan(ops.Calls.IndexOf("snapshot"));
        ops.Calls.IndexOf("snapshot").ShouldBeLessThan(ops.Calls.IndexOf("provision"));
        ops.Calls.IndexOf("reassign:green-1").ShouldBeLessThan(ops.Calls.IndexOf("destroy:old-1"));
        ops.Calls.ShouldContain("capacity");
        ops.Calls.ShouldContain("destroy:old-1");
        ops.Calls.ShouldNotContain("destroy:green-1");
    }

    [Fact]
    public async Task Post_cutover_health_failure_reassigns_ip_back_and_rolls_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = new RecordingOps { HealthyTrueCount = 1 };
        var (cloud, finalStatus) = await DriveAsync(ops, ct);

        finalStatus.ShouldBe(SagaStatus.MigrateRolledBack);

        Db.ChangeTracker.Clear();
        var reloaded = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloaded.ProvisioningStatus.ShouldBe(SagaStatus.Succeeded);

        ops.Calls.ShouldContain("reassign:green-1"); // cutover to green
        ops.Calls.ShouldContain("reassign:old-1");   // reassigned back to old
        ops.Calls.ShouldContain("destroy:green-1");  // discard failed green
        ops.Calls.ShouldNotContain("destroy:old-1"); // old is preserved
    }

    [Fact]
    public async Task Insufficient_capacity_fails_before_snapshotting()
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = new RecordingOps { HasCapacity = false };
        var (cloud, finalStatus) = await DriveAsync(ops, ct);

        finalStatus.ShouldBe(SagaStatus.FailedMigrate);
        ops.Calls.ShouldNotContain("snapshot");

        Db.ChangeTracker.Clear();
        var reloaded = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloaded.ProvisioningStatus.ShouldBe(SagaStatus.Succeeded);
    }

    private async Task<(Cloud cloud, string finalStatus)> DriveAsync(RecordingOps ops, CancellationToken ct)
    {
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.MigrateQuiescing, provider: "stub", kind: SagaKinds.Migrate, ct: ct);

        var creds = new FakeCredentials();
        var conns = new FakeConnections();

        for (var i = 0; i < 12 && !Terminal.Contains(job.Status); i++)
        {
            var handler = new BlueGreenHandler(
                job.Status, Db, Clock, creds, conns, [ops], NullLogger<BlueGreenHandler>.Instance);
            await handler.HandleAsync(job, ct);
            Db.ChangeTracker.Clear();
            job = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        }

        return (cloud, job.Status);
    }

    private sealed class RecordingOps : IBlueGreenOperations
    {
        public List<string> Calls { get; } = [];
        public bool HasCapacity { get; init; } = true;
        public int HealthyTrueCount { get; init; } = int.MaxValue;
        private int healthCalls;

        public string Provider => "stub";

        public Task<bool> HasCapacityAsync(string accessToken, CancellationToken ct)
        {
            Calls.Add("capacity");
            return Task.FromResult(HasCapacity);
        }

        public Task<string> FindActiveDropletIdAsync(string accessToken, Cloud cloud, CancellationToken ct)
        {
            Calls.Add("find_old");
            return Task.FromResult("old-1");
        }

        public Task<string> SnapshotDataVolumeAsync(string accessToken, Cloud cloud, CancellationToken ct)
        {
            Calls.Add("snapshot");
            return Task.FromResult("snap-1");
        }

        public Task<GreenDroplet> ProvisionGreenAsync(
            string accessToken, Cloud cloud, string snapshotId, string targetVersion, CancellationToken ct)
        {
            Calls.Add("provision");
            return Task.FromResult(new GreenDroplet("green-1", "203.0.113.2"));
        }

        public Task<bool> HealthyAsync(string ipv4, CancellationToken ct)
        {
            healthCalls++;
            Calls.Add("health");
            return Task.FromResult(healthCalls <= HealthyTrueCount);
        }

        public Task ReassignReservedIpAsync(string accessToken, Cloud cloud, string dropletId, CancellationToken ct)
        {
            Calls.Add($"reassign:{dropletId}");
            return Task.CompletedTask;
        }

        public Task DestroyDropletAsync(string accessToken, string dropletId, CancellationToken ct)
        {
            Calls.Add($"destroy:{dropletId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentials : ISagaCredentialSource
    {
        public Task<bool> TryGetDekAsync(Cloud cloud, byte[] dekDestination, CancellationToken ct)
        {
            Array.Fill(dekDestination, (byte)1);
            return Task.FromResult(true);
        }

        public Task CaptureForSagaAsync(Cloud cloud, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConnections : IDigitalOceanOAuthConnections
    {
        public Task<string?> GetAccessTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct) =>
            Task.FromResult<string?>("do-access-token");

        public Task SaveAsync(Guid userId, string accessToken, string refreshToken, Instant accessExpiresAt,
            ReadOnlyMemory<byte> dek, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DigitalOceanConnectionInfo?> GetInfoAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetRefreshTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct) => throw new NotSupportedException();
        public Task SetConnectionStatusAsync(Guid userId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }
}
