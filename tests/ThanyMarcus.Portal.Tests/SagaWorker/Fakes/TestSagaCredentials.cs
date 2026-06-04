using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

internal static class TestSagaCredentials
{
    public static ISagaCredentialSource Source(PortalDbContext db, IDataProtectionProvider dp, IClock clock) =>
        new SagaCredentialSource(
            new PostgresInfraOpUnlockCache(db, dp, clock),
            new PostgresSagaCredentialGrantStore(db, dp, clock),
            clock);

    public static ISagaCredentialGrantStore GrantStore(PortalDbContext db, IDataProtectionProvider dp, IClock clock) =>
        new PostgresSagaCredentialGrantStore(db, dp, clock);
}
