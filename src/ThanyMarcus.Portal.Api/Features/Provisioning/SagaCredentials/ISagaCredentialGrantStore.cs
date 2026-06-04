using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public interface ISagaCredentialGrantStore
{
    Task PutAsync(Guid cloudId, ReadOnlyMemory<byte> dek, Instant expiresAt, CancellationToken ct);

    Task<bool> TryGetAsync(Guid cloudId, byte[] dekDestination, CancellationToken ct);

    Task DeleteAsync(Guid cloudId, CancellationToken ct);
}
