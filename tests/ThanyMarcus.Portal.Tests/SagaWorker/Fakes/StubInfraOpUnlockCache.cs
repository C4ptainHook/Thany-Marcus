using System.Diagnostics.CodeAnalysis;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

public sealed class StubInfraOpUnlockCache : IInfraOpUnlockCache
{
    private readonly byte[] dek;

    public StubInfraOpUnlockCache(byte fill = 0x42)
    {
        dek = new byte[32];
        Array.Fill(dek, fill);
    }

    public Task<bool> TryGetAsync(Guid userId, byte[] dekDestination, CancellationToken ct)
    {
        if (dekDestination.Length != dek.Length) return Task.FromResult(false);
        dek.AsSpan().CopyTo(dekDestination);
        return Task.FromResult(true);
    }

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Matches interface")]
    public Task SetAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct) => Task.CompletedTask;

    public Task InvalidateAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
