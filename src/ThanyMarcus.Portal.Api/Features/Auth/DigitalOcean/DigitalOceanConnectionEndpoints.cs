using System.Security.Claims;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public static class DigitalOceanConnectionEndpoints
{
    public static void MapDigitalOceanConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/clouds/connections/digitalocean", async (
            ClaimsPrincipal user,
            IDigitalOceanOAuthConnections connections,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var info = await connections.GetInfoAsync(userId, ct);
            if (info is null)
                return Results.Ok(new { connected = false });
            return Results.Ok(new
            {
                connected = true,
                status    = info.ConnectionStatus,
                expiresAt = info.AccessExpiresAt.ToString(),
            });
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapDelete("/api/clouds/connections/digitalocean", async (
            ClaimsPrincipal user,
            IDigitalOceanOAuthConnections connections,
            IDigitalOceanOAuthClient doClient,
            IInfraOpUnlockCache unlockCache,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            try
            {
                if (await unlockCache.TryGetAsync(userId, dek, ct))
                {
                    var access = await connections.GetAccessTokenAsync(userId, dek, ct);
                    if (access is not null)
                    {
                        try { await doClient.RevokeOAuthTokenAsync(access, ct); }
                        catch { /* best-effort */ }
                    }
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(dek);
            }
            await connections.DisconnectAsync(userId, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
