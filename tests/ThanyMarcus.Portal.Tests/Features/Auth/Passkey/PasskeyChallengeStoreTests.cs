using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Passkey;

public sealed class PasskeyChallengeStoreTests
{
    private static PasskeyChallengeStore Create(FakeClock clock, int ttlSeconds = 300)
        => new(clock, Options.Create(new PasskeyConfiguration { ChallengeTtlSeconds = ttlSeconds }));

    private static FakeClock NewClock() => new(Instant.FromUtc(2026, 5, 31, 12, 0));

    [Fact]
    public void Stash_returns_unique_ids()
    {
        var store = Create(NewClock());
        var a = store.Stash("{}", Guid.CreateVersion7(), PasskeyChallengeKind.Register);
        var b = store.Stash("{}", Guid.CreateVersion7(), PasskeyChallengeKind.Register);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Take_consumes_single_use()
    {
        var store = Create(NewClock());
        var userId = Guid.CreateVersion7();
        var id = store.Stash("{\"x\":1}", userId, PasskeyChallengeKind.Login);

        var first = store.Take(id);
        first.ShouldNotBeNull();
        first!.OptionsJson.ShouldBe("{\"x\":1}");
        first.UserId.ShouldBe(userId);
        first.Kind.ShouldBe(PasskeyChallengeKind.Login);

        store.Take(id).ShouldBeNull();
    }

    [Fact]
    public void Take_returns_null_for_unknown_id()
        => Create(NewClock()).Take(Guid.NewGuid()).ShouldBeNull();

    [Fact]
    public void Take_returns_null_when_expired()
    {
        var clock = NewClock();
        var store = Create(clock, ttlSeconds: 300);
        var id = store.Stash("{}", null, PasskeyChallengeKind.Login);

        clock.Advance(Duration.FromSeconds(301));

        store.Take(id).ShouldBeNull();
    }

    [Fact]
    public void SweepExpired_removes_expired_entries()
    {
        var clock = NewClock();
        var store = Create(clock, ttlSeconds: 300);
        store.Stash("{}", null, PasskeyChallengeKind.Login);
        store.Stash("{}", null, PasskeyChallengeKind.Login);

        clock.Advance(Duration.FromSeconds(301));

        store.SweepExpired().ShouldBe(2);
        store.SweepExpired().ShouldBe(0);
    }

    [Fact]
    public void SweepExpired_keeps_live_entries()
    {
        var clock = NewClock();
        var store = Create(clock, ttlSeconds: 300);
        var id = store.Stash("{}", null, PasskeyChallengeKind.Login);

        clock.Advance(Duration.FromSeconds(60));

        store.SweepExpired().ShouldBe(0);
        store.Take(id).ShouldNotBeNull();
    }
}
