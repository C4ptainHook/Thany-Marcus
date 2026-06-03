using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public static class TotpEndpoints
{
    public static void MapTotpEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth/totp").RequireAuthorization();

        grp.MapPost("/enable/init", (
            ClaimsPrincipal user,
            TotpService totp) =>
        {
            var label = user.FindFirstValue(ClaimTypes.Email)
                ?? user.FindFirstValue(AuthClaimTypes.Username)!;
            var secret = totp.GenerateSecret();
            var qr = totp.BuildQrPngDataUri(secret, label);
            return Results.Ok(new TotpEnableInitResponse(secret, qr));
        });

        grp.MapPost("/enable/verify", async (
            TotpEnableVerifyRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            IInfraOpUnlockCache unlockCache,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!totp.Verify(body.Secret, body.Code))
                return Results.BadRequest(new { error = "invalid_code" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var now = clock.GetCurrentInstant();

            var row = await db.TotpSecrets.SingleOrDefaultAsync(t => t.UserId == userId, ct);

            if (row is { EnabledAt: not null, DisabledAt: null })
            {
                if (string.IsNullOrWhiteSpace(body.CurrentCode))
                    return Results.Json(new { error = "current_code_required" }, statusCode: StatusCodes.Status401Unauthorized);
                var current = await totp.VerifyChallengeAsync(db, userId, body.CurrentCode, ct);
                if (current is TotpChallengeResult.Failed)
                    return Results.Json(new { error = "invalid_current_code" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (ciphertext, nonce, tag) = totp.Encrypt(body.Secret);
            if (row is null)
            {
                row = new TotpSecret
                {
                    UserId = userId,
                    Ciphertext = ciphertext,
                    Nonce = nonce,
                    Tag = tag,
                    EnabledAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.TotpSecrets.Add(row);
            }
            else
            {
                row.Ciphertext = ciphertext;
                row.Nonce = nonce;
                row.Tag = tag;
                row.EnabledAt = now;
                row.DisabledAt = null;
                row.UpdatedAt = now;
            }

            var account = await db.Users.SingleAsync(u => u.Id == userId, ct);
            var dek = new byte[32];
            try
            {
                if (!await unlockCache.TryGetAsync(userId, dek, ct))
                    return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);

                var (wrappedDek, wrapNonce, wrapTag) = totp.WrapDek(body.Secret, dek);
                account.TotpWrappedDek = wrappedDek;
                account.TotpWrapNonce = wrapNonce;
                account.TotpWrapTag = wrapTag;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
            await db.SaveChangesAsync(ct);

            var backupCodes = await backups.IssueAsync(userId, ct);

            await RefreshTotpClaim(http, user, TotpClaimValues.Verified);

            return Results.Ok(new TotpEnableVerifyResponse(backupCodes));
        })
           .AddEndpointFilter<RequireInfraOpUnlockFilter>();

        grp.MapPost("/disable", async (
            TotpDisableRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var result = await totp.VerifyChallengeAsync(db, userId, body.Code, ct);
            if (result is TotpChallengeResult.Failed)
                return Results.Unauthorized();

            var row = await db.TotpSecrets.SingleAsync(t => t.UserId == userId, ct);
            row.DisabledAt = clock.GetCurrentInstant();

            // The TOTP recovery path is gone with the secret; clear the wrapped DEK so the
            // forgot-passphrase page hides the TOTP option for this user.
            var account = await db.Users.SingleAsync(u => u.Id == userId, ct);
            account.TotpWrappedDek = null;
            account.TotpWrapNonce = null;
            account.TotpWrapTag = null;

            await db.SaveChangesAsync(ct);
            await backups.PurgeUnusedAsync(userId, ct);

            await RefreshTotpClaim(http, user, TotpClaimValues.NotEnabled);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapPost("/totp-challenge", async (
            TotpChallengeRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            AuthLockoutService lockouts,
            CancellationToken ct) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            var totpOk = await totp.VerifyChallengeAsync(db, userId, body.Code, ct);
            var ok = totpOk is TotpChallengeResult.Verified
                  || await backups.RedeemAsync(userId, body.Code, ct);
            if (!ok)
            {
                await lockouts.RecordFailureAsync(userId, AuthLockoutKinds.Totp, ct);
                return Results.Unauthorized();
            }

            await lockouts.ClearAsync(userId, AuthLockoutKinds.Totp, ct);
            await RefreshTotpClaim(http, user, TotpClaimValues.Verified);
            return Results.NoContent();
        })
           .RequireAuthorization()
           .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge)
           .AddEndpointFilter<LockoutGuardFilter>()
           .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Totp))
           .AddEndpointFilter<RequireTurnstileFilter>()
           .WithMetadata(new TurnstileKindMetadata(AuthLockoutKinds.Totp));
    }

    private static async Task RefreshTotpClaim(HttpContext http, ClaimsPrincipal user, string totpValue)
    {
        var identity = (ClaimsIdentity)user.Identity!;
        foreach (var existing in identity.FindAll(AuthClaimTypes.Totp).ToList())
            identity.RemoveClaim(existing);
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, totpValue));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user);
    }
}
