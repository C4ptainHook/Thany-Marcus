using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens;

public static partial class PluginTokenEndpoints
{
    public static void MapPluginTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/clouds/{id:guid}/plugin-tokens", async (
            Guid id,
            ClaimsPrincipal user,
            PortalDbContext db,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId) return Results.Forbid();

            var active = await db.PluginTokenMetadata
                .Where(p => p.CloudId == id && p.RevokedAt == null)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new PluginTokenSummary(p.Id, p.Name, p.CreatedAt, p.LastUsedAt))
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new PluginTokenStateResponse(active));
        })
        .RequireAuthorization();

        app.MapPost("/api/clouds/{id:guid}/plugin-tokens", async (
            Guid id,
            ClaimsPrincipal user,
            PortalDbContext db,
            IClock clock,
            ICloudAdminTokenAccessor adminTokens,
            IPortalToCloudPluginTokenClient cloudClient,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId) return Results.Forbid();

            var adminToken = await adminTokens.GetPlaintextAsync(cloud.Id, ct);
            if (string.IsNullOrEmpty(adminToken))
            {
                return Results.Problem(
                    "cloud admin token missing — cannot sync new plugin token to cloud",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var now = clock.GetCurrentInstant();
            var existing = await db.PluginTokenMetadata
                .Where(p => p.CloudId == id && p.RevokedAt == null)
                .ToListAsync(ct);

            var raw = "tm_" + RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var log = loggerFactory.CreateLogger("PluginTokenEndpoints");

            try
            {
                await cloudClient.PostAsync(
                    $"https://{cloud.Hostname}/admin/plugin-tokens",
                    adminToken, hash, "plugin", ct);
            }
            catch (PluginTokenSyncException ex)
            {
                LogSyncFailure(log, ex, id, ex.StatusCode);
                var status = ex.StatusCode == 401
                    ? StatusCodes.Status502BadGateway
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Problem(
                    $"cloud sync failed: {ex.Message}",
                    statusCode: status);
            }

            foreach (var e in existing)
            {
                try
                {
                    await cloudClient.RevokeAsync(
                        $"https://{cloud.Hostname}/admin/plugin-tokens/revoke",
                        adminToken, e.TokenHash, ct);
                }
                catch (PluginTokenSyncException ex)
                {
                    LogRevokeFailure(log, ex, id, ex.StatusCode);
                }
                e.RevokedAt = now;
                e.UpdatedAt = now;
            }

            db.PluginTokenMetadata.Add(new PluginTokenMetadata
            {
                CloudId   = id,
                Name      = "plugin",
                TokenHash = hash,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(new PluginTokenIssuedResponse(
                Token:     raw,
                CloudUrl:  $"https://{cloud.Hostname}",
                IssuedAt:  now));
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Re-issue plugin token: cloud POST failed for cloud {CloudId} (status={StatusCode})")]
    private static partial void LogSyncFailure(ILogger logger, Exception ex, Guid cloudId, int? statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Re-issue plugin token: cloud revoke failed for cloud {CloudId} (status={StatusCode}); portal metadata still marked revoked")]
    private static partial void LogRevokeFailure(ILogger logger, Exception ex, Guid cloudId, int? statusCode);
}

public sealed record PluginTokenSummary(Guid Id, string Name, Instant CreatedAt, Instant? LastUsedAt);
public sealed record PluginTokenStateResponse(PluginTokenSummary? Active);
public sealed record PluginTokenIssuedResponse(string Token, string CloudUrl, Instant IssuedAt);
