using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed record EmergencyKitResponse(string RecoveryString, string QrPngDataUri, Instant GeneratedAt);

public sealed record EmergencyKitStatusResponse(Instant? GeneratedAt, Instant? LastUsedAt, bool TotpRecoveryAvailable);

public static class EmergencyKitEndpoints
{
    public static void MapEmergencyKitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/emergency-kit").RequireAuthorization();

        group.MapPost("/generate", async (
            ClaimsPrincipal user,
            EmergencyKitService kits,
            IInfraOpUnlockCache cache,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            try
            {
                if (!await cache.TryGetAsync(userId, dek, ct))
                    return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);

                var phrase = await kits.GenerateAsync(userId, dek, ct);
                return Results.Ok(new EmergencyKitResponse(
                    phrase, EmergencyKitService.BuildQrPngDataUri(phrase), clock.GetCurrentInstant()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
           .RequireAuthorization(AuthPolicies.TotpRequired)
           .AddEndpointFilter<RequireInfraOpUnlockFilter>();

        group.MapGet("", async (
            ClaimsPrincipal user,
            EmergencyKitService kits,
            PortalDbContext db,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var active = await kits.GetActiveAsync(userId, ct);
            var lastUsed = await db.EmergencyKits
                .Where(k => k.UserId == userId && k.UsedAt != null)
                .MaxAsync(k => (Instant?)k.UsedAt, ct);
            var totpRecovery = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.TotpWrappedDek != null)
                .SingleAsync(ct);

            return Results.Ok(new EmergencyKitStatusResponse(active?.CreatedAt, lastUsed, totpRecovery));
        });
    }
}
