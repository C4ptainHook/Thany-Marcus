using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class PassphraseEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = "alice@example.com",
            Name = "Alice",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    [Fact]
    public async Task Init_then_unlock_succeeds_and_seeds_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var init = await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative),
            new PassphraseInitRequest("hunter2hunter2"),
            ct);
        init.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Db.ChangeTracker.Clear();
        var stored = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        stored.PassphraseWrappedDek.ShouldNotBeNull();
        stored.PassphraseSetAt.ShouldNotBeNull();

        var unlock = await client.PostAsJsonAsync(
            new Uri("/api/auth/unlock", UriKind.Relative),
            new PassphraseUnlockRequest("hunter2hunter2"),
            ct);
        unlock.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
        var probe = new byte[32];
        (await cache.TryGetAsync(user.Id, probe, ct)).ShouldBeTrue();
        probe.Any(b => b != 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_second_time_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        (await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative),
            new PassphraseInitRequest("hunter2hunter2"),
            ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative),
            new PassphraseInitRequest("another-pass"),
            ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await second.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("passphrase_already_set");
    }

    [Fact]
    public async Task Unlock_with_wrong_passphrase_returns_401_and_does_not_seed_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative),
            new PassphraseInitRequest("hunter2hunter2"),
            ct)).EnsureSuccessStatusCode();

        var bad = await client.PostAsJsonAsync(
            new Uri("/api/auth/unlock", UriKind.Relative),
            new PassphraseUnlockRequest("wrong"),
            ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await bad.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("invalid_passphrase");

        await using var scope = factory.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_requires_TotpRequired_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative),
            new PassphraseInitRequest("hunter2hunter2"),
            ct);
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unlock_requires_TotpRequired_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri("/api/auth/unlock", UriKind.Relative),
            new PassphraseUnlockRequest("anything"),
            ct);
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed record ErrorBody(string Error);
}
