using ThanyMarcus.Cloud.Api.Features.Bootstrap;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public static class AdminHealthEndpoint
{
    public static void MapAdminHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/health", async (
            CaddyHealthReader caddy,
            BootstrapState bootstrap,
            BootstrapOptions opts,
            CancellationToken ct) =>
        {
            var certReady = await caddy.IsCertReadyAsync(opts.Hostname, ct).ConfigureAwait(false);
            return Results.Ok(new CloudAdminHealthResponse(
                CertReady:          certReady,
                CloudId:            opts.CloudId,
                RegistrationStatus: bootstrap.RegistrationStatus,
                ApiVersion:         CloudAdminHealthResponse.CurrentApiVersion));
        })
        .WithName("AdminHealth")
        .AllowAnonymous();
}
