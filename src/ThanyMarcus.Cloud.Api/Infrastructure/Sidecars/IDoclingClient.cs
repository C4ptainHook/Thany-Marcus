namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IDoclingClient
{
    Task<string> ExtractMarkdownAsync(string storageKey, string mimeType, CancellationToken ct);
}
