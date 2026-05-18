namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public interface IImageExtractor
{
    Task<string?> ExtractAsync(string storageKey, CancellationToken ct);
}

public sealed class NotImplementedImageExtractor : IImageExtractor
{
    public Task<string?> ExtractAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException("Image OCR is owned by CLOUD-003.");
}
