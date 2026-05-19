using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

public interface ICloudSecretBundle
{
    Task PutAsync(Guid cloudId, string kind, string plaintext, ReadOnlyMemory<byte> dek, Instant? expiresAt, CancellationToken ct);
    Task<string> GetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<string?> TryGetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<Instant?> GetExpiresAtAsync(Guid cloudId, string kind, CancellationToken ct);
    Task DeleteAsync(Guid cloudId, string kind, CancellationToken ct);
    Task DeleteAllForCloudAsync(Guid cloudId, CancellationToken ct);
}

public sealed class CloudSecretNotFoundException(Guid cloudId, string kind)
    : InvalidOperationException($"cloud_secret_not_found: cloud_id={cloudId} kind={kind}")
{
    public Guid CloudId { get; } = cloudId;
    public string Kind { get; } = kind;
}
