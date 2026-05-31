namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public interface IUrlExtractor
{
    Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct);
}

public sealed record UrlExtractionResult(
    string CanonicalUrl,
    string? Title,
    string? Description,
    string? AuthorName,
    string? ProviderName,
    string? ThumbnailUrl,
    int HttpStatus,
    string? MinimalReason)
{
    public static UrlExtractionResult Minimal(Uri uri, string reason) => new(
        CanonicalUrl: uri.ToString(),
        Title: null,
        Description: null,
        AuthorName: null,
        ProviderName: null,
        ThumbnailUrl: null,
        HttpStatus: 0,
        MinimalReason: reason);

    public static UrlExtractionResult FromMetaHead(Uri uri, ParsedHead head, int httpStatus) => new(
        CanonicalUrl: uri.ToString(),
        Title: head.Title,
        Description: head.Description,
        AuthorName: head.Author,
        ProviderName: head.SiteName,
        ThumbnailUrl: head.ImageUrl,
        HttpStatus: httpStatus,
        MinimalReason: null);

    public static UrlExtractionResult FromOEmbed(Uri uri, ParsedHead head, OEmbedResponse oe, int httpStatus) => new(
        CanonicalUrl: uri.ToString(),
        Title: oe.Title ?? head.Title,
        Description: head.Description,
        AuthorName: oe.AuthorName ?? head.Author,
        ProviderName: oe.ProviderName ?? head.SiteName,
        ThumbnailUrl: oe.ThumbnailUrl ?? head.ImageUrl,
        HttpStatus: httpStatus,
        MinimalReason: null);
}
