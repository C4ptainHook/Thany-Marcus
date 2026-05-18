namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public interface IFileExtractor
{
    Task<string?> ExtractAsync(string storageKey, string? mimeType, CancellationToken ct);
}

public sealed class NotImplementedFileExtractor : IFileExtractor
{
    public Task<string?> ExtractAsync(string storageKey, string? mimeType, CancellationToken ct) =>
        throw new NotImplementedException("File extraction is owned by CLOUD-003.");
}
