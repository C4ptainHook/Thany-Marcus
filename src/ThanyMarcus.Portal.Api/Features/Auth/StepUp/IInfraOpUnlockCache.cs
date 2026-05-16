using System.Diagnostics.CodeAnalysis;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public interface IInfraOpUnlockCache
{
    Task<bool> TryGetAsync(Guid userId, byte[] dekDestination, CancellationToken ct);

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Set matches the cache semantics; not consumed from VB.")]
    Task SetAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct);

    Task InvalidateAsync(Guid userId, CancellationToken ct);
}
