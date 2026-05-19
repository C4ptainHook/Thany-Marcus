using System.Security.Claims;
using System.Security.Cryptography;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public static class ProviderTokenEndpoints
{
    public static void MapProviderTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/clouds/provider-tokens")
            .RequireAuthorization(AuthPolicies.TotpRequired);

        grp.MapGet("", async (
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var list = await vault.ListAsync(userId, ct);
            return Results.Ok(list);
        });

        grp.MapPost("", async (
            RegisterProviderTokenRequest body,
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            IInfraOpUnlockCache cache,
            CancellationToken ct) =>
        {
            if (!KnownProviders.IsValid(body.Provider))
                return Results.BadRequest(new { error = "unknown_provider", supported = KnownProviders.All });
            if (body.Provider == KnownProviders.DigitalOcean)
                return Results.BadRequest(new { error = "use_oauth_flow", start = "/oauth/digitalocean/start" });
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { error = "token_required" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            try
            {
                if (!await cache.TryGetAsync(userId, dek, ct))
                    return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);

                if (body.Replace)
                {
                    await vault.ReplaceAsync(userId, body.Provider, body.Token, dek, ct);
                }
                else
                {
                    try
                    {
                        await vault.AddAsync(userId, body.Provider, body.Token, dek, ct);
                    }
                    catch (ProviderTokenAlreadyExistsException)
                    {
                        return Results.Conflict(new { error = "provider_token_already_set", provider = body.Provider });
                    }
                }
                return Results.NoContent();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();

        grp.MapDelete("{provider}", async (
            string provider,
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            CancellationToken ct) =>
        {
            if (!KnownProviders.IsValid(provider))
                return Results.BadRequest(new { error = "unknown_provider" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            await vault.RemoveAsync(userId, provider, ct);
            return Results.NoContent();
        })
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
