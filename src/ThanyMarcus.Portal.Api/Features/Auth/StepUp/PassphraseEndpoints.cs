using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.Login;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public static class PassphraseEndpoints
{
    public static void MapPassphraseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/passphrase/init", async (
            PassphraseInitRequest body,
            ClaimsPrincipal user,
            PassphraseService svc,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var ok = await svc.InitAsync(userId, body.Passphrase, ct);
            return ok ? Results.NoContent() : Results.Conflict(new { error = "passphrase_already_set" });
        }).RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapPost("/api/auth/unlock", async (
            PassphraseUnlockRequest body,
            ClaimsPrincipal user,
            PassphraseService svc,
            IInfraOpUnlockCache cache,
            AuthLockoutService lockouts,
            DigitalOceanTokenRefresher doRefresher,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            try
            {
                var result = await svc.TryUnwrapDekAsync(userId, body.Passphrase, dek, ct);
                if (result is UnlockResult.Failed)
                {
                    await lockouts.RecordFailureAsync(userId, AuthLockoutKinds.Unlock, ct);
                    return Results.Json(new { error = "invalid_passphrase" }, statusCode: StatusCodes.Status401Unauthorized);
                }
                await cache.SetAsync(userId, dek, ct);
                await lockouts.ClearAsync(userId, AuthLockoutKinds.Unlock, ct);
                await doRefresher.RefreshExpiringAsync(userId, dek, ct);
                return Results.NoContent();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
           .RequireAuthorization(AuthPolicies.TotpRequired)
           .RequireRateLimiting(AuthRateLimiterPolicies.Unlock)
           .AddEndpointFilter<LockoutGuardFilter>()
           .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Unlock))
           .AddEndpointFilter<RequireTurnstileFilter>()
           .WithMetadata(new TurnstileKindMetadata(AuthLockoutKinds.Unlock));
    }
}
