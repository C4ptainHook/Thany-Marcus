using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class SagaCredentialGrantTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Grant_store_round_trips_the_sealed_dek()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);
        var store = TestSagaCredentials.GrantStore(Db, dp, Clock);

        var dek = SagaTestSeed.MakeDek(0x37);
        await store.PutAsync(cloud.Id, dek, Clock.GetCurrentInstant() + Duration.FromHours(6), ct);

        var got = new byte[32];
        (await store.TryGetAsync(cloud.Id, got, ct)).ShouldBeTrue();
        got.ShouldBe(dek);
    }

    [Fact]
    public async Task Grant_store_seals_at_rest_so_a_raw_db_read_yields_no_dek()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);
        var store = TestSagaCredentials.GrantStore(Db, dp, Clock);

        var dek = SagaTestSeed.MakeDek();
        await store.PutAsync(cloud.Id, dek, Clock.GetCurrentInstant() + Duration.FromHours(6), ct);
        Db.ChangeTracker.Clear();

        var row = await Db.SagaCredentialGrants.AsNoTracking().SingleAsync(g => g.CloudId == cloud.Id, ct);
        row.SealedDek.ShouldNotBe(dek);
        row.SealedDek.AsSpan().IndexOf(dek).ShouldBe(-1);
    }

    [Fact]
    public async Task Expired_grant_is_not_returned_and_is_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);
        var store = TestSagaCredentials.GrantStore(Db, dp, Clock);

        await store.PutAsync(cloud.Id, SagaTestSeed.MakeDek(),
            Clock.GetCurrentInstant() - Duration.FromMinutes(1), ct);

        (await store.TryGetAsync(cloud.Id, new byte[32], ct)).ShouldBeFalse();
        (await Db.SagaCredentialGrants.AnyAsync(g => g.CloudId == cloud.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Source_falls_back_to_grant_when_interactive_unlock_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);

        var dek = SagaTestSeed.MakeDek(0x5A);
        await TestSagaCredentials.GrantStore(Db, dp, Clock)
            .PutAsync(cloud.Id, dek, Clock.GetCurrentInstant() + Duration.FromHours(6), ct);

        var source = TestSagaCredentials.Source(Db, dp, Clock);
        var got = new byte[32];
        (await source.TryGetDekAsync(cloud, got, ct)).ShouldBeTrue();
        got.ShouldBe(dek);
    }

    [Fact]
    public async Task Source_prefers_interactive_unlock_over_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: true, ct: ct);

        await TestSagaCredentials.GrantStore(Db, dp, Clock)
            .PutAsync(cloud.Id, SagaTestSeed.MakeDek(0x99), Clock.GetCurrentInstant() + Duration.FromHours(6), ct);

        var source = TestSagaCredentials.Source(Db, dp, Clock);
        var got = new byte[32];
        (await source.TryGetDekAsync(cloud, got, ct)).ShouldBeTrue();
        got.ShouldBe(SagaTestSeed.MakeDek());
    }

    [Fact]
    public async Task Source_returns_false_with_neither_unlock_nor_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);

        var source = TestSagaCredentials.Source(Db, dp, Clock);
        (await source.TryGetDekAsync(cloud, new byte[32], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureForSaga_seals_the_live_unlock_dek_into_a_readable_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, _) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: true, ct: ct);

        await TestSagaCredentials.Source(Db, dp, Clock).CaptureForSagaAsync(cloud, ct);
        Db.ChangeTracker.Clear();

        var got = new byte[32];
        (await TestSagaCredentials.GrantStore(Db, dp, Clock).TryGetAsync(cloud.Id, got, ct)).ShouldBeTrue();
        got.ShouldBe(SagaTestSeed.MakeDek());

        var grant = await Db.SagaCredentialGrants.AsNoTracking().SingleAsync(g => g.CloudId == cloud.Id, ct);
        grant.ExpiresAt.ShouldBe(Clock.GetCurrentInstant() + SagaCredentialGrant.Ttl);
    }

    [Fact]
    public async Task Sweep_deletes_grants_for_terminal_clouds_and_expired_grants()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var store = TestSagaCredentials.GrantStore(Db, dp, Clock);

        var (_, terminalCloud, _) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.Succeeded, seedUnlock: false, ct: ct);
        var (_, expiredCloud, _) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.TfApplying, seedUnlock: false, ct: ct);
        var (_, liveCloud, _) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.TfApplying, seedUnlock: false, ct: ct);

        await store.PutAsync(terminalCloud.Id, SagaTestSeed.MakeDek(),
            Clock.GetCurrentInstant() + Duration.FromHours(6), ct);
        await store.PutAsync(expiredCloud.Id, SagaTestSeed.MakeDek(),
            Clock.GetCurrentInstant() - Duration.FromMinutes(1), ct);
        await store.PutAsync(liveCloud.Id, SagaTestSeed.MakeDek(),
            Clock.GetCurrentInstant() + Duration.FromHours(6), ct);

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Clock);
        services.AddDbContext<PortalDbContext>(o => o
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention());
        await using var sp = services.BuildServiceProvider();

        await new SagaCredentialGrantSweepService(sp, Clock).SweepOnceAsync(ct);
        Db.ChangeTracker.Clear();

        var survivors = await Db.SagaCredentialGrants.Select(g => g.CloudId).ToListAsync(ct);
        survivors.ShouldBe([liveCloud.Id]);
    }
}
