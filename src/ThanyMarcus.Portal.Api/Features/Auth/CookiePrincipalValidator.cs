using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public enum CookieValidationOutcome { Pass, Reject }

public sealed class CookiePrincipalValidator(PortalDbContext db, IInfraOpUnlockCache cache)
{
    public async Task<CookieValidationOutcome> ValidateAsync(
        ClaimsPrincipal principal,
        DateTimeOffset? issuedUtc,
        CancellationToken ct = default)
    {
        var subUs = principal.FindFirst(AuthClaimTypes.SubUs)?.Value;
        if (subUs is null || !Guid.TryParse(subUs, out var userId))
            return CookieValidationOutcome.Reject;

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            await cache.InvalidateAsync(userId, ct);
            return CookieValidationOutcome.Reject;
        }

        if (user.SessionsInvalidatedAt is { } invalidatedAt
            && issuedUtc is { } issued
            && Instant.FromDateTimeOffset(issued) < invalidatedAt)
        {
            await cache.InvalidateAsync(userId, ct);
            return CookieValidationOutcome.Reject;
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        if (user.Email is { } email)
            ReplaceClaim(identity, ClaimTypes.Email, email);
        else
            RemoveClaim(identity, ClaimTypes.Email);
        ReplaceClaim(identity, AuthClaimTypes.Username, user.Username);
        ReplaceClaim(identity, ClaimTypes.Name, user.Name);
        if (user.ProfilePictureUrl is { } pic)
            ReplaceClaim(identity, "picture", pic);
        else
            RemoveClaim(identity, "picture");

        var current = identity.FindFirst(AuthClaimTypes.Totp)?.Value;
        var totp = await ResolveTotpClaimAsync(userId, current, ct);
        ReplaceClaim(identity, AuthClaimTypes.Totp, totp);

        return CookieValidationOutcome.Pass;
    }

    private async Task<string> ResolveTotpClaimAsync(Guid userId, string? current, CancellationToken ct)
    {
        var enabled = await db.TotpSecrets
            .AnyAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
        if (!enabled) return TotpClaimValues.NotEnabled;
        return current == TotpClaimValues.Verified ? TotpClaimValues.Verified : TotpClaimValues.NotVerified;
    }

    private static void ReplaceClaim(ClaimsIdentity identity, string type, string value)
    {
        RemoveClaim(identity, type);
        identity.AddClaim(new Claim(type, value));
    }

    private static void RemoveClaim(ClaimsIdentity identity, string type)
    {
        foreach (var existing in identity.FindAll(type).ToList())
            identity.RemoveClaim(existing);
    }
}
