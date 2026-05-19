using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class DoclingHttpClient : IDoclingClient
{
    public const string HttpClientName = "Docling";

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory clientFactory;
    private readonly IArtifactStore store;
    private readonly DoclingOptions options;
    private readonly ILogger<DoclingHttpClient> log;

    public DoclingHttpClient(
        IHttpClientFactory clientFactory,
        IArtifactStore store,
        DoclingOptions options,
        ILogger<DoclingHttpClient> log)
    {
        this.clientFactory = clientFactory;
        this.store = store;
        this.options = options;
        this.log = log;
    }

    public async Task<string> ExtractMarkdownAsync(string storageKey, string mimeType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        _ = mimeType;

        var presigned = await store.IssueDownloadUrlAsync(storageKey, options.PresignedUrlTtl, ct);

        var body = new DoclingConvertSourceRequest(new HttpSource(presigned.Url.ToString()));
        using var http = clientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ConvertSourcePath)
        {
            Content = JsonContent.Create(body),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DoclingClientException("docling request failed: " + ex.Message, ex);
        }

        using var _resp = resp;
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            LogFailure(log, (int)resp.StatusCode, storageKey);
            if (IsTransient(resp.StatusCode))
            {
                throw new HttpRequestException(
                    $"docling transient {(int)resp.StatusCode}: {Truncate(errBody)}");
            }
            throw new DoclingClientException(
                $"docling returned {(int)resp.StatusCode}",
                (int)resp.StatusCode,
                Truncate(errBody));
        }

        DoclingConvertResponse? parsed;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<DoclingConvertResponse>(ResponseJson, ct);
        }
        catch (JsonException ex)
        {
            throw new DoclingClientException("docling response was not valid JSON", ex);
        }

        var markdown = parsed?.Document?.MdContent;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new DoclingClientException(
                "docling response missing document.md_content");
        }
        return markdown;
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code >= 500 || code == HttpStatusCode.RequestTimeout || code == HttpStatusCode.TooManyRequests;

    private static string Truncate(string body) =>
        body.Length <= 512 ? body : body[..512] + "...";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DoclingHttpClient non-success: status={Status} storage_key={Key}")]
    private static partial void LogFailure(ILogger logger, int status, string key);

    internal sealed record DoclingConvertSourceRequest(
        [property: JsonPropertyName("http_source")] HttpSource HttpSource);

    internal sealed record HttpSource(
        [property: JsonPropertyName("url")] string Url);

    internal sealed record DoclingConvertResponse(
        [property: JsonPropertyName("document")] DoclingDocument? Document);

    internal sealed record DoclingDocument(
        [property: JsonPropertyName("md_content")] string? MdContent,
        [property: JsonPropertyName("filename")] string? Filename);
}
