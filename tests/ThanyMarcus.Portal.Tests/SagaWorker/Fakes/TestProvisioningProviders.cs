using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

internal static class TestProvisioningProviders
{
    public static IProvisioningProviderRegistry Registry(
        PortalDbContext db, IClock clock, IDigitalOceanOAuthClient? doClient = null) =>
        new ProvisioningProviderRegistry(
        [
            new StubProvisioningProvider(),
            new DigitalOceanProvisioningProvider(
                new DigitalOceanOAuthConnections(db, clock),
                new CloudSecretBundle(db, clock),
                doClient ?? new FakeDigitalOceanOAuthClient(),
                NullLogger<DigitalOceanProvisioningProvider>.Instance),
        ]);
}
