using ThanyMarcus.Cloud.Api.Features.Bootstrap;
using ThanyMarcus.Cloud.Api.Features.Update;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public static class AdminHealthEndpoint
{
    public static void MapAdminHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/health", (
            CertFileReader cert,
            BootstrapState bootstrap,
            BootstrapOptions opts,
            CloudVersionReader version) =>
        {
            var certReady = cert.IsCertReady();
            return Results.Ok(new CloudAdminHealthResponse(
                CertReady:          certReady,
                CloudId:            opts.CloudId,
                RegistrationStatus: bootstrap.RegistrationStatus,
                ApiVersion:         CloudAdminHealthResponse.CurrentApiVersion,
                CurrentVersion:     version.CurrentVersion()));
        })
        .WithName("AdminHealth")
        .AllowAnonymous();
}
