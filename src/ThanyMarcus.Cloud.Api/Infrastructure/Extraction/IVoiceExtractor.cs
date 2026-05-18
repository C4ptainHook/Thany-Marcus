namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public interface IVoiceExtractor
{
    Task<string?> ExtractAsync(string storageKey, CancellationToken ct);
}

public sealed class NotImplementedVoiceExtractor : IVoiceExtractor
{
    public Task<string?> ExtractAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException("Voice transcription is owned by CLOUD-016b/c.");
}
