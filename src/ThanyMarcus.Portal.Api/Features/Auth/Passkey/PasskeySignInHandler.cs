using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

/// <summary>
/// Issues the session cookie after a successful passkey assertion, producing the same
/// principal shape Google SSO does (minus the OAuth bootstrap). TOTP still gates the
/// session: an enabled-but-unverified TOTP lands the user in the pending_totp state.
/// </summary>
public sealed class PasskeySignInHandler(PortalDbContext db, IClock clock)
{
    public async Task SignInAsync(HttpContext http, User user, CancellationToken ct = default)
    {
        user.LastSeenAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);

        var totp = await ResolveTotpClaimAsync(user.Id, ct);

        var identity = new ClaimsIdentity(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        if (user.GoogleSubject is { } googleSub)
            identity.AddClaim(new Claim(AuthClaimTypes.SubGoogle, googleSub));
        identity.AddClaim(new Claim(AuthClaimTypes.Username, user.Username));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, totp));
        if (user.Email is { } email)
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Name));
        if (user.ProfilePictureUrl is { } picture)
            identity.AddClaim(new Claim("picture", picture));

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

    private async Task<string> ResolveTotpClaimAsync(Guid userId, CancellationToken ct)
    {
        var enabled = await db.TotpSecrets
            .AnyAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
        return enabled ? TotpClaimValues.NotVerified : TotpClaimValues.NotEnabled;
    }
}
