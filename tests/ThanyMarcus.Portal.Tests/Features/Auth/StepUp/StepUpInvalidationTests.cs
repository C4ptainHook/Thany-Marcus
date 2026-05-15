using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class StepUpInvalidationTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
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
    public async Task Signout_invalidates_the_step_up_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var cache = factory.Services.GetRequiredService<IInfraOpUnlockCache>();
        var dek = new byte[32];
        Array.Fill(dek, (byte)0xAB);
        cache.Set(user.Id, dek);
        cache.TryGet(user.Id, new byte[32]).ShouldBeTrue();

        var res = await client.PostAsync(new Uri("/api/auth/signout", UriKind.Relative), content: null, ct);
        res.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        cache.TryGet(user.Id, new byte[32]).ShouldBeFalse();
    }

    [Fact]
    public async Task Cookie_validator_invalidates_cache_when_user_not_in_db()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghostUserId = Guid.CreateVersion7();

        var cache = new InProcessInfraOpUnlockCache(Clock);
        cache.Set(ghostUserId, new byte[32]);
        cache.TryGet(ghostUserId, new byte[32]).ShouldBeTrue();

        var validator = new CookiePrincipalValidator(Db, cache);
        var identity = new ClaimsIdentity("Cookies");
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, ghostUserId.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, TotpClaimValues.NotEnabled));

        var outcome = await validator.ValidateAsync(
            new ClaimsPrincipal(identity),
            Clock.GetCurrentInstant().ToDateTimeOffset(),
            ct);

        outcome.ShouldBe(CookieValidationOutcome.Reject);
        cache.TryGet(ghostUserId, new byte[32]).ShouldBeFalse();
    }

    [Fact]
    public async Task Cookie_validator_invalidates_cache_when_sessions_invalidated_bumped()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalidatedAt = Clock.GetCurrentInstant();
        var user = await InsertUserAsync();
        user.SessionsInvalidatedAt = invalidatedAt;
        Db.Users.Update(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cache = new InProcessInfraOpUnlockCache(Clock);
        cache.Set(user.Id, new byte[32]);
        cache.TryGet(user.Id, new byte[32]).ShouldBeTrue();

        var validator = new CookiePrincipalValidator(Db, cache);
        var identity = new ClaimsIdentity("Cookies");
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, TotpClaimValues.NotEnabled));
        var issued = (invalidatedAt - Duration.FromMinutes(5)).ToDateTimeOffset();

        var outcome = await validator.ValidateAsync(new ClaimsPrincipal(identity), issued, ct);

        outcome.ShouldBe(CookieValidationOutcome.Reject);
        cache.TryGet(user.Id, new byte[32]).ShouldBeFalse();
    }
}
