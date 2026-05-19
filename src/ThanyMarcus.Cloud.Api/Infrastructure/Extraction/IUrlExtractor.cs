namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public interface IUrlExtractor
{
    Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct);
}

public sealed record UrlExtractionResult(
    string Markdown,
    string? CanonicalUrl,
    string? Title,
    string? Byline,
    string? Excerpt,
    string? PublishedAt,
    string? Lang,
    int HttpStatus,
    bool Truncated);
