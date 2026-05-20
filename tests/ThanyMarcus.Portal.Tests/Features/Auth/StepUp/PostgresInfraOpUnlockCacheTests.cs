using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class PostgresInfraOpUnlockCacheTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private PostgresInfraOpUnlockCache CreateCache(IDataProtectionProvider? dp = null) =>
        new(Db, dp ?? new EphemeralDataProtectionProvider(), Clock);

    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"u-{Guid.NewGuid():N}@example.com",
            Name = "U",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private static byte[] MakeDek(byte fill = 0x42)
    {
        var dek = new byte[32];
        Array.Fill(dek, fill);
        return dek;
    }

    [Fact]
    public async Task TryGetAsync_on_empty_store_returns_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = CreateCache();

        var ok = await cache.TryGetAsync(Guid.NewGuid(), new byte[32], ct);

        ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Set_then_TryGet_copies_the_dek_into_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        var dek = MakeDek(0xAB);

        await cache.SetAsync(user.Id, dek, ct);

        var dest = new byte[32];
        var ok = await cache.TryGetAsync(user.Id, dest, ct);

        ok.ShouldBeTrue();
        dest.ShouldBe(dek);
    }

    [Fact]
    public async Task TryGet_with_wrong_length_destination_returns_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(user.Id, MakeDek(), ct);

        var ok = await cache.TryGetAsync(user.Id, new byte[16], ct);

        ok.ShouldBeFalse();
    }

    [Fact]
    public async Task TryGet_on_expired_entry_returns_false_and_deletes_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(user.Id, MakeDek(), ct);

        Clock.Advance(PostgresInfraOpUnlockCache.SlidingTtl + Duration.FromMinutes(1));

        var ok = await cache.TryGetAsync(user.Id, new byte[32], ct);

        ok.ShouldBeFalse();
        Db.ChangeTracker.Clear();
        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == user.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryGet_slides_expires_at_forward_on_each_hit()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(user.Id, MakeDek(), ct);

        var halfTtl = Duration.FromTicks(PostgresInfraOpUnlockCache.SlidingTtl.BclCompatibleTicks / 2);

        Clock.Advance(halfTtl);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeTrue();

        Clock.Advance(halfTtl);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeTrue();

        Clock.Advance(PostgresInfraOpUnlockCache.SlidingTtl + Duration.FromMinutes(1));
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Invalidate_removes_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(user.Id, MakeDek(), ct);
        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == user.Id, ct)).ShouldBeTrue();

        await cache.InvalidateAsync(user.Id, ct);

        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == user.Id, ct)).ShouldBeFalse();
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_service_deletes_expired_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var stale = await InsertUserAsync();
        var fresh = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(stale.Id, MakeDek(0x01), ct);

        var almostTtl = PostgresInfraOpUnlockCache.SlidingTtl - Duration.FromMinutes(1);
        Clock.Advance(almostTtl);
        await cache.SetAsync(fresh.Id, MakeDek(0x02), ct);

        Clock.Advance(Duration.FromMinutes(2));

        var services = new ServiceCollection();
        services.AddDbContext<PortalDbContext>(opts => opts
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention());
        await using var sp = services.BuildServiceProvider();
        var sweep = new InfraOpUnlockSweepService(sp, Clock);
        await sweep.SweepOnceAsync(ct);

        Db.ChangeTracker.Clear();
        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == stale.Id, ct)).ShouldBeFalse();
        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == fresh.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Set_twice_replaces_the_ciphertext_and_resets_ttl()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = CreateCache();
        await cache.SetAsync(user.Id, MakeDek(0xAA), ct);

        Clock.Advance(Duration.FromMinutes(9));
        await cache.SetAsync(user.Id, MakeDek(0xBB), ct);

        Clock.Advance(Duration.FromMinutes(5));
        var dest = new byte[32];
        (await cache.TryGetAsync(user.Id, dest, ct)).ShouldBeTrue();
        dest.ShouldAllBe(b => b == 0xBB);
    }
}
