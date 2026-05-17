using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class CrashRecoveryTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Stale_tf_applying_row_is_reset_and_becomes_claimable()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfApplying,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.ClaimedBy = "dead-worker";
        tracked.LeaseExpiresAt = Clock.GetCurrentInstant() - Duration.FromMinutes(5);
        tracked.NextVisibleAt = Clock.GetCurrentInstant() + Duration.FromMinutes(5);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Clock);
        services.AddSingleton(new TimestampInterceptor(Clock));
        services.AddDbContext<PortalDbContext>(opts => opts
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TimestampInterceptor(Clock)));

        await using var sp = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkspaceBase"] = Path.Combine(Path.GetTempPath(), "crash-test-" + Guid.NewGuid().ToString("N")),
                ["Provisioning:TerraformModulesDir"] = Path.Combine(Path.GetTempPath(), "crash-test-mods-" + Guid.NewGuid().ToString("N")),
            }).Build();
        var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        var service = new CrashRecoveryService(sp, layout, Clock, NullLogger<CrashRecoveryService>.Instance);
        await service.StartAsync(ct);

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.ClaimedBy.ShouldBeNull();
        reloaded.LeaseExpiresAt.ShouldBeNull();
        reloaded.NextVisibleAt.ShouldBeLessThanOrEqualTo(Clock.GetCurrentInstant());
    }
}
