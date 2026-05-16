using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderMeta;

public static class ProviderMetaEndpoints
{
    public static void MapProviderMetaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/clouds/provider-meta/digitalocean", () =>
        {
            var regions = DigitalOceanRegions.Catalog
                .Select(r => new { slug = r.Slug, label = r.Label, continent = r.Continent })
                .ToArray();
            return Results.Ok(new { regions });
        })
        .AllowAnonymous();
    }
}
