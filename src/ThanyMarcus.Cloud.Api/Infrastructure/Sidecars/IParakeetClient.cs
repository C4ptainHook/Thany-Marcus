namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IParakeetClient
{
    Task<ParakeetTranscript> TranscribeAsync(string storageKey, CancellationToken ct);
}

public sealed record ParakeetTranscript(
    string Text,
    string? LanguageDetected);
