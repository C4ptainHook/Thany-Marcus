using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed record ResetViaTotpRequest(string TotpCode, string NewPassphrase);

public sealed record ResetViaKitRequest(string RecoveryString, string NewPassphrase);

public static class PassphraseResetEndpoints
{
    public static void MapPassphraseResetEndpoints(this IEndpointRouteBuilder app)
    {
        // Reset a forgotten passphrase by proving possession of the TOTP second factor.
        // Does NOT touch the Emergency Kit — that path stays valid.
        app.MapPost("/api/auth/passphrase/reset-via-totp", async (
            ResetViaTotpRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            TotpService totp,
            PassphraseService passphrase,
            PassphraseValidator validator,
            IInfraOpUnlockCache cache,
            AuthLockoutService lockouts,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            var validation = validator.Validate(body.NewPassphrase);
            if (validation != PassphraseValidationResult.Ok)
                return Results.BadRequest(new { error = "passphrase_invalid", reason = Reason(validation) });

            var totpResult = await totp.VerifyChallengeAsync(db, userId, body.TotpCode, ct);
            if (totpResult is TotpChallengeResult.Failed)
            {
                await lockouts.RecordFailureAsync(userId, AuthLockoutKinds.Totp, ct);
                return Results.Json(new { error = "invalid_totp_code" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            await lockouts.ClearAsync(userId, AuthLockoutKinds.Totp, ct);

            var account = await db.Users.SingleAsync(u => u.Id == userId, ct);
            var secretRow = await db.TotpSecrets
                .SingleOrDefaultAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
            if (account.TotpWrappedDek is null or { Length: 0 } || secretRow is null)
                return Results.Json(new { error = "totp_recovery_unavailable" }, statusCode: StatusCodes.Status409Conflict);

            var secret = totp.Decrypt(secretRow.Ciphertext);
            var dek = new byte[32];
            try
            {
                if (!totp.TryUnwrapDek(secret, account.TotpWrappedDek!, account.TotpWrapNonce!, account.TotpWrapTag!, dek))
                    return Results.Json(new { error = "totp_recovery_unavailable" }, statusCode: StatusCodes.Status409Conflict);
                await passphrase.RewrapAsync(userId, dek, body.NewPassphrase, ct);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }

            await cache.InvalidateAsync(userId, ct);
            return Results.NoContent();
        })
           .RequireAuthorization()
           .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge);

        // Reset a forgotten passphrase by redeeming the Emergency Kit. Single-use: the kit is
        // consumed and a fresh one is minted and returned for the caller to save.
        app.MapPost("/api/auth/passphrase/reset-via-kit", async (
            ResetViaKitRequest body,
            ClaimsPrincipal user,
            EmergencyKitService kits,
            PassphraseService passphrase,
            PassphraseValidator validator,
            IInfraOpUnlockCache cache,
            PortalDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            var validation = validator.Validate(body.NewPassphrase);
            if (validation != PassphraseValidationResult.Ok)
                return Results.BadRequest(new { error = "passphrase_invalid", reason = Reason(validation) });

            var dek = new byte[32];
            try
            {
                var kit = await kits.TryUnlockAsync(userId, body.RecoveryString, dek, ct);
                if (kit is null)
                    return Results.Json(new { error = "invalid_recovery_string" }, statusCode: StatusCodes.Status401Unauthorized);

                kit.UsedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(ct);

                await passphrase.RewrapAsync(userId, dek, body.NewPassphrase, ct);
                var phrase = await kits.GenerateAsync(userId, dek, ct);
                await cache.InvalidateAsync(userId, ct);

                return Results.Ok(new EmergencyKitResponse(
                    phrase, EmergencyKitService.BuildQrPngDataUri(phrase), clock.GetCurrentInstant()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
           .RequireAuthorization()
           .RequireRateLimiting(AuthRateLimiterPolicies.Unlock);
    }

    private static string Reason(PassphraseValidationResult result) => result switch
    {
        PassphraseValidationResult.TooShort => "too_short",
        PassphraseValidationResult.TooCommon => "too_common",
        _ => "invalid",
    };
}
