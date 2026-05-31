using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class GoogleSignInHandler(PortalDbContext db, IClock clock)
{
    public async Task HandleAsync(ClaimsIdentity identity, JsonElement userInfo, CancellationToken ct = default)
    {
        var googleSub = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Google ticket missing sub claim");
        var email = identity.FindFirst(ClaimTypes.Email)?.Value
            ?? throw new InvalidOperationException("Google ticket missing email claim");
        var name = identity.FindFirst(ClaimTypes.Name)?.Value ?? email;
        string? picture = userInfo.ValueKind == JsonValueKind.Object
            && userInfo.TryGetProperty("picture", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        var now = clock.GetCurrentInstant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == googleSub, ct);
        if (user is null)
        {
            user = new User
            {
                GoogleSubject = googleSub,
                Email = email,
                Username = await GenerateUsernameAsync(email, ct),
                Name = name,
                ProfilePictureUrl = picture,
                LastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.Email = email;
            user.Name = name;
            user.ProfilePictureUrl = picture;
            user.LastSeenAt = now;
        }
        await db.SaveChangesAsync(ct);

        var totp = await ResolveTotpClaimAsync(user.Id, ct);

        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.SubGoogle, googleSub));
        identity.AddClaim(new Claim(AuthClaimTypes.Username, user.Username));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, totp));
        if (picture is not null)
            identity.AddClaim(new Claim("picture", picture));
    }

    private async Task<string> ResolveTotpClaimAsync(Guid userId, CancellationToken ct)
    {
        var enabled = await db.TotpSecrets
            .AnyAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
        return enabled ? TotpClaimValues.NotVerified : TotpClaimValues.NotEnabled;
    }

    private async Task<string> GenerateUsernameAsync(string email, CancellationToken ct)
    {
        var local = email.Split('@', 2)[0];
        var sanitized = new string(local.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray())
            .Trim('_', '-')
            .ToLowerInvariant();
        var seed = UsernameValidator.Validate(sanitized).IsOk ? sanitized : "user";

        var candidate = seed;
        for (var n = 1; await db.Users.AnyAsync(u => EF.Functions.ILike(u.Username, candidate), ct); n++)
        {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            var trimmed = seed.Length + suffix.Length > 32 ? seed[..(32 - suffix.Length)] : seed;
            candidate = trimmed + suffix;
        }
        return candidate;
    }
}
