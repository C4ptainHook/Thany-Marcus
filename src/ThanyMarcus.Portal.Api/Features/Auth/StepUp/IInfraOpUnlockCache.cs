using System.Diagnostics.CodeAnalysis;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public interface IInfraOpUnlockCache
{
    bool TryGet(Guid userId, Span<byte> dekDestination);

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Set matches the cache semantics; not consumed from VB.")]
    void Set(Guid userId, ReadOnlySpan<byte> dek);

    void Invalidate(Guid userId);
}
