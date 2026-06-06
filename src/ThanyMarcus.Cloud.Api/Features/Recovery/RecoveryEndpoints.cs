using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.CloudRecovery;

namespace ThanyMarcus.Cloud.Api.Features.Recovery;

public static class RecoveryEndpoints
{
    private const string CodePrefix = "tmr_";
    private const string RecoveredLabel = "plugin-recovered";

    public static void MapRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/recovery-code", ProvisionAsync)
           .AddEndpointFilter<RequirePluginAuthFilter>()
           .WithName("ProvisionRecoveryCode")
           .Produces<ProvisionRecoveryCodeResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status401Unauthorized)
           .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost("/api/recovery/redeem", RedeemAsync)
           .AllowAnonymous()
           .WithName("RedeemRecovery")
           .Produces<RedeemRecoveryResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status401Unauthorized)
           .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> ProvisionAsync(CloudDbContext db, IClock clock, CancellationToken ct)
    {
        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        if (settings.RecoveryAnchorHash is { Length: > 0 })
            return Results.Problem("recovery_already_provisioned", statusCode: StatusCodes.Status409Conflict);

        var code = NewCode();
        settings.RecoveryAnchorHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        settings.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ProvisionRecoveryCodeResponse(code));
    }

    private static async Task<IResult> RedeemAsync(
        RedeemRecoveryRequest req,
        HttpContext http,
        CloudDbContext db,
        IClock clock,
        RecoveryThrottle throttle,
        CancellationToken ct)
    {
        var key = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!throttle.TryConsume(key))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        if (string.IsNullOrWhiteSpace(req.RecoveryCode))
            return Results.Unauthorized();

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        if (settings.RecoveryAnchorHash is not { Length: > 0 })
            return Results.Unauthorized();

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(req.RecoveryCode));
        if (presented.Length != settings.RecoveryAnchorHash.Length ||
            !CryptographicOperations.FixedTimeEquals(presented, settings.RecoveryAnchorHash))
            return Results.Unauthorized();

        var now = clock.GetCurrentInstant();

        var rawToken = "tm_" + RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)),
            Label     = RecoveredLabel,
            CreatedAt = now,
        });

        var newCode = NewCode();
        settings.RecoveryAnchorHash = SHA256.HashData(Encoding.UTF8.GetBytes(newCode));
        settings.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        throttle.Reset(key);
        return Results.Ok(new RedeemRecoveryResponse(rawToken, newCode));
    }

    private static string NewCode() => CodePrefix + RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
}
