using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Login;

public sealed partial class DigitalOceanTokenRefresher(
    PortalDbContext db,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthClient doClient,
    IClock clock,
    ILogger<DigitalOceanTokenRefresher> log)
{
    private static readonly Duration RefreshWindow = Duration.FromDays(5);

    public async Task RefreshExpiringAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var doClouds = await db.Clouds
            .IgnoreQueryFilters()
            .Where(c => c.UserId == userId
                     && c.Provider == KnownProviders.DigitalOcean
                     && c.DestroyedAt == null)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var now = clock.GetCurrentInstant();

        foreach (var cloudId in doClouds)
        {
            var expiresAt = await secrets.GetExpiresAtAsync(cloudId, CloudSecretKind.DoOAuthAccess, ct);
            if (expiresAt is null) continue;
            if (expiresAt.Value > now.Plus(RefreshWindow)) continue;

            try
            {
                var refreshToken = await secrets.TryGetAsync(cloudId, CloudSecretKind.DoOAuthRefresh, dek, ct);
                if (refreshToken is null)
                {
                    await MarkNeedsReauthAsync(cloudId, ct);
                    continue;
                }
                var fresh = await doClient.RefreshAsync(refreshToken, ct);
                var newExpiresAt = now.Plus(Duration.FromSeconds(fresh.ExpiresIn));
                await secrets.PutAsync(cloudId, CloudSecretKind.DoOAuthAccess,  fresh.AccessToken,  dek, newExpiresAt, ct);
                await secrets.PutAsync(cloudId, CloudSecretKind.DoOAuthRefresh, fresh.RefreshToken, dek, null,         ct);
                await MarkConnectedAsync(cloudId, ct);
            }
            catch (DigitalOceanOAuthRefreshFailedException ex)
            {
                LogRefreshFailed(log, ex, cloudId, ex.StatusCode);
                await MarkNeedsReauthAsync(cloudId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRefreshError(log, ex, cloudId);
                await MarkNeedsReauthAsync(cloudId, ct);
            }
        }
    }

    private async Task MarkNeedsReauthAsync(Guid cloudId, CancellationToken ct)
    {
        await db.Clouds.IgnoreQueryFilters()
            .Where(c => c.Id == cloudId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConnectionStatus, "needs_reauth"), ct);
    }

    private async Task MarkConnectedAsync(Guid cloudId, CancellationToken ct)
    {
        await db.Clouds.IgnoreQueryFilters()
            .Where(c => c.Id == cloudId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConnectionStatus, "connected"), ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DigitalOcean token refresh failed for cloud {CloudId} (status {StatusCode}); marking needs_reauth")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex, Guid cloudId, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "DigitalOcean token refresh threw for cloud {CloudId}; marking needs_reauth")]
    private static partial void LogRefreshError(ILogger logger, Exception ex, Guid cloudId);
}
