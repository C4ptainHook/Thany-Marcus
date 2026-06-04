using System.Security.Cryptography;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public interface ISagaCredentialSource
{
    Task<bool> TryGetDekAsync(Cloud cloud, byte[] dekDestination, CancellationToken ct);

    Task CaptureForSagaAsync(Cloud cloud, CancellationToken ct);
}

public sealed class SagaCredentialSource(
    IInfraOpUnlockCache unlockCache,
    ISagaCredentialGrantStore grants,
    IClock clock) : ISagaCredentialSource
{
    public async Task<bool> TryGetDekAsync(Cloud cloud, byte[] dekDestination, CancellationToken ct)
    {
        if (await unlockCache.TryGetAsync(cloud.UserId, dekDestination, ct))
            return true;
        return await grants.TryGetAsync(cloud.Id, dekDestination, ct);
    }

    public async Task CaptureForSagaAsync(Cloud cloud, CancellationToken ct)
    {
        var dek = new byte[32];
        try
        {
            if (await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
                await grants.PutAsync(cloud.Id, dek, clock.GetCurrentInstant() + SagaCredentialGrant.Ttl, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
