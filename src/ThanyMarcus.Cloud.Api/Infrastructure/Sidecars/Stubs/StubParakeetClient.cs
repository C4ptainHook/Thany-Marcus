namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubParakeetClient : IParakeetClient
{
    // FORK: stub; real impl lands in handoffs #4-#6
    public Task<string> TranscribeAsync(string storageKey, CancellationToken ct) =>
        Task.FromResult("[stub parakeet transcription]");
}
