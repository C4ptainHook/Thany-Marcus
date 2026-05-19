using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

namespace ThanyMarcus.Portal.Api.Features.Auth.Login;

public sealed partial class DigitalOceanTokenRefresher(
    IDigitalOceanOAuthConnections connections,
    IDigitalOceanOAuthClient doClient,
    IClock clock,
    ILogger<DigitalOceanTokenRefresher> log)
{
    private static readonly Duration RefreshWindow = Duration.FromDays(5);

    public async Task RefreshExpiringAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var info = await connections.GetInfoAsync(userId, ct);
        if (info is null) return;

        var now = clock.GetCurrentInstant();
        if (info.AccessExpiresAt > now.Plus(RefreshWindow))
        {
            // already fresh — make sure status reflects connected (in case it was previously flipped)
            if (info.ConnectionStatus != DigitalOceanConnectionStatus.Connected)
                await connections.SetConnectionStatusAsync(userId, DigitalOceanConnectionStatus.Connected, ct);
            return;
        }

        try
        {
            var refreshToken = await connections.GetRefreshTokenAsync(userId, dek, ct);
            if (refreshToken is null)
            {
                await connections.SetConnectionStatusAsync(userId, DigitalOceanConnectionStatus.NeedsReauth, ct);
                return;
            }
            var fresh = await doClient.RefreshAsync(refreshToken, ct);
            var newExpiresAt = now.Plus(Duration.FromSeconds(fresh.ExpiresIn));
            await connections.SaveAsync(userId, fresh.AccessToken, fresh.RefreshToken, newExpiresAt, dek, ct);
        }
        catch (DigitalOceanOAuthRefreshFailedException ex)
        {
            LogRefreshFailed(log, ex, userId, ex.StatusCode);
            await connections.SetConnectionStatusAsync(userId, DigitalOceanConnectionStatus.NeedsReauth, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshError(log, ex, userId);
            await connections.SetConnectionStatusAsync(userId, DigitalOceanConnectionStatus.NeedsReauth, ct);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DigitalOcean token refresh failed for user {UserId} (status {StatusCode}); marking needs_reauth")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex, Guid userId, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "DigitalOcean token refresh threw for user {UserId}; marking needs_reauth")]
    private static partial void LogRefreshError(ILogger logger, Exception ex, Guid userId);
}
