namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IUrlFetcherClient
{
    Task<string> FetchMarkdownAsync(string url, CancellationToken ct);
}
