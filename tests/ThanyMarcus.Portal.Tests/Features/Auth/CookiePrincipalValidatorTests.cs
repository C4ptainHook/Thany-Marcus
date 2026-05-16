using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class CookiePrincipalValidatorTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private IInfraOpUnlockCache Cache => _cache ??= new PostgresInfraOpUnlockCache(Db, new EphemeralDataProtectionProvider(), Clock);
    private PostgresInfraOpUnlockCache? _cache;

    private static ClaimsIdentity BuildIdentity(
        Guid? userId,
        string? totp = TotpClaimValues.NotEnabled,
        string email = "old@example.com",
        string name = "Old")
    {
        var identity = new ClaimsIdentity("Cookies");
        if (userId is { } id)
            identity.AddClaim(new Claim(AuthClaimTypes.SubUs, id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        if (totp is not null)
            identity.AddClaim(new Claim(AuthClaimTypes.Totp, totp));
        return identity;
    }

    private async Task<User> InsertUserAsync(
        Instant? sessionsInvalidatedAt = null,
        string email = "alice@example.com",
        string name = "Alice",
        string? picture = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = email,
            Name = name,
            ProfilePictureUrl = picture,
            SessionsInvalidatedAt = sessionsInvalidatedAt,
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
    public async Task Missing_sub_us_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var validator = new CookiePrincipalValidator(Db, Cache);
        var principal = new ClaimsPrincipal(BuildIdentity(userId: null));

        var outcome = await validator.ValidateAsync(principal, DateTimeOffset.UtcNow, ct);
        outcome.ShouldBe(CookieValidationOutcome.Reject);
    }

    [Fact]
    public async Task User_not_in_db_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var validator = new CookiePrincipalValidator(Db, Cache);
        var principal = new ClaimsPrincipal(BuildIdentity(Guid.CreateVersion7()));

        var outcome = await validator.ValidateAsync(principal, DateTimeOffset.UtcNow, ct);
        outcome.ShouldBe(CookieValidationOutcome.Reject);
    }

    [Fact]
    public async Task Issued_before_sessions_invalidated_at_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalidatedAt = Clock.GetCurrentInstant();
        var user = await InsertUserAsync(sessionsInvalidatedAt: invalidatedAt);
        var issued = invalidatedAt - Duration.FromMinutes(5);

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id);
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, issued.ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Reject);
        identity.FindFirst(ClaimTypes.Email)!.Value.ShouldBe("old@example.com");
        identity.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("Old");
    }

    [Fact]
    public async Task Issued_after_sessions_invalidated_at_passes_and_refreshes_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalidatedAt = Clock.GetCurrentInstant() - Duration.FromHours(1);
        var user = await InsertUserAsync(
            sessionsInvalidatedAt: invalidatedAt,
            email: "fresh@example.com",
            name: "Fresh Name",
            picture: "https://example.com/fresh.png");

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id);
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, Clock.GetCurrentInstant().ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Pass);
        identity.FindFirst(ClaimTypes.Email)!.Value.ShouldBe("fresh@example.com");
        identity.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("Fresh Name");
        identity.FindFirst("picture")!.Value.ShouldBe("https://example.com/fresh.png");
    }

    [Fact]
    public async Task Email_changes_propagate_on_next_validate()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync(email: "new@example.com", name: "New");

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id, email: "stale@example.com", name: "Stale");
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, Clock.GetCurrentInstant().ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Pass);
        identity.FindFirst(ClaimTypes.Email)!.Value.ShouldBe("new@example.com");
        identity.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("New");
    }

    [Fact]
    public async Task Totp_disabled_mid_session_drops_claim_to_not_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id, totp: TotpClaimValues.Verified);
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, Clock.GetCurrentInstant().ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Pass);
        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotEnabled);
    }

    [Fact]
    public async Task Verified_claim_preserved_across_requests_when_totp_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var now = Clock.GetCurrentInstant();
        Db.TotpSecrets.Add(new TotpSecret
        {
            UserId = user.Id,
            Ciphertext = [1],
            Nonce = [2],
            Tag = [3],
            EnabledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id, totp: TotpClaimValues.Verified);
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, now.ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Pass);
        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.Verified);
    }

    [Fact]
    public async Task Not_verified_stays_not_verified_when_totp_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var now = Clock.GetCurrentInstant();
        Db.TotpSecrets.Add(new TotpSecret
        {
            UserId = user.Id,
            Ciphertext = [1],
            Nonce = [2],
            Tag = [3],
            EnabledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var validator = new CookiePrincipalValidator(Db, Cache);
        var identity = BuildIdentity(user.Id, totp: TotpClaimValues.NotVerified);
        var principal = new ClaimsPrincipal(identity);

        var outcome = await validator.ValidateAsync(principal, now.ToDateTimeOffset(), ct);
        outcome.ShouldBe(CookieValidationOutcome.Pass);
        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotVerified);
    }
}
