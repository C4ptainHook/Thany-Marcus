using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dek = new byte[32];
        Array.Fill(dek, (byte)0xAB);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var seed = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
            await seed.SetAsync(user.Id, dek, ct);
        }
        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == user.Id, ct)).ShouldBeTrue();

        var res = await client.PostAsync(new Uri("/api/auth/signout", UriKind.Relative), content: null, ct);
        res.StatusCode.ShouldBe(HttpStatusCode.Found);

        (await Db.StepUpUnlocks.AnyAsync(u => u.UserId == user.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cookie_validator_invalidates_cache_when_user_not_in_db()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cache = new PostgresInfraOpUnlockCache(Db, new EphemeralDataProtectionProvider(), Clock);
        await cache.SetAsync(user.Id, new byte[32], ct);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeTrue();

        Db.Users.Remove(await Db.Users.SingleAsync(u => u.Id == user.Id, ct));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var validator = new CookiePrincipalValidator(Db, cache);
        var identity = new ClaimsIdentity("Cookies");
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, TotpClaimValues.NotEnabled));

        var outcome = await validator.ValidateAsync(
            new ClaimsPrincipal(identity),
            Clock.GetCurrentInstant().ToDateTimeOffset(),
            ct);

        outcome.ShouldBe(CookieValidationOutcome.Reject);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeFalse();
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

        var cache = new PostgresInfraOpUnlockCache(Db, new EphemeralDataProtectionProvider(), Clock);
        await cache.SetAsync(user.Id, new byte[32], ct);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeTrue();

        var validator = new CookiePrincipalValidator(Db, cache);
        var identity = new ClaimsIdentity("Cookies");
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, TotpClaimValues.NotEnabled));
        var issued = (invalidatedAt - Duration.FromMinutes(5)).ToDateTimeOffset();

        var outcome = await validator.ValidateAsync(new ClaimsPrincipal(identity), issued, ct);

        outcome.ShouldBe(CookieValidationOutcome.Reject);
        (await cache.TryGetAsync(user.Id, new byte[32], ct)).ShouldBeFalse();
    }
}
