using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Admin;

public static class PricingBackfillEndpoint
{
    public static void MapPricingBackfillEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/pricing/backfill", async (
            ClaimsPrincipal principal,
            PortalDbContext db,
            IInfraOpUnlockCache unlockCache,
            IProvisioningProviderRegistry providers,
            IDigitalOceanOAuthConnections connections,
            IDoSizesCatalog catalog,
            IClock clock,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(AuthClaimTypes.SubUs)!);

            var defaultSize = config.GetValue("Provisioning:DefaultSize", "s-1vcpu-1gb")!;

            var pricingProviders = providers.All
                .Where(p => p.SupportsPricing)
                .Select(p => p.Key)
                .ToList();

            var clouds = await db.Clouds
                .Where(c => c.UserId == userId
                         && pricingProviders.Contains(c.Provider)
                         && c.ProvisioningStatus == SagaStatus.Succeeded
                         && c.PriceMonthlyUsd == null)
                .ToListAsync(ct);

            if (clouds.Count == 0)
                return Results.Ok(new { stamped = 0, skipped = 0 });

            var dek = new byte[32];
            try
            {
                if (!await unlockCache.TryGetAsync(userId, dek, ct))
                    return Results.StatusCode(StatusCodes.Status423Locked);

                var accessToken = await connections.GetAccessTokenAsync(userId, dek, ct);
                if (string.IsNullOrEmpty(accessToken))
                    return Results.BadRequest(new { error = "digitalocean_not_connected" });

                var stamped = 0;
                var skipped = 0;
                foreach (var cloud in clouds)
                {
                    var size = await catalog.GetAsync(defaultSize, accessToken, ct);
                    if (size is null)
                    {
                        skipped++;
                        continue;
                    }
                    cloud.PriceMonthlyUsd = size.MonthlyUsd;
                    cloud.PriceHourlyUsd  = size.HourlyUsd;
                    cloud.PriceCurrency   = "USD";
                    cloud.PricedAt        = clock.GetCurrentInstant();
                    cloud.PricedSource    = "do_api_v2_sizes_backfill";
                    stamped++;
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { stamped, skipped });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
