using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class ParakeetHttpClient : IParakeetClient
{
    public const string HttpClientName = "Parakeet";

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory clientFactory;
    private readonly IArtifactStore store;
    private readonly ParakeetOptions options;
    private readonly ILogger<ParakeetHttpClient> log;

    public ParakeetHttpClient(
        IHttpClientFactory clientFactory,
        IArtifactStore store,
        ParakeetOptions options,
        ILogger<ParakeetHttpClient> log)
    {
        this.clientFactory = clientFactory;
        this.store = store;
        this.options = options;
        this.log = log;
    }

    public async Task<ParakeetTranscript> TranscribeAsync(string storageKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var presigned = await store.IssueDownloadUrlAsync(storageKey, options.PresignedUrlTtl, ct);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(presigned.Url.ToString()), "url" },
        };
        using var http = clientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, options.TranscribePath)
        {
            Content = form,
        };

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ParakeetClientException("parakeet request failed: " + ex.Message, ex);
        }
        using var _resp = resp;

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            LogFailure(log, (int)resp.StatusCode, storageKey);
            if (IsTransient(resp.StatusCode))
            {
                throw new HttpRequestException(
                    $"parakeet transient {(int)resp.StatusCode}: {Truncate(errBody)}");
            }
            throw new ParakeetClientException(
                $"parakeet returned {(int)resp.StatusCode}",
                (int)resp.StatusCode,
                Truncate(errBody));
        }

        ParakeetResponse? parsed;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<ParakeetResponse>(ResponseJson, ct);
        }
        catch (JsonException ex)
        {
            throw new ParakeetClientException("parakeet response was not valid JSON", ex);
        }
        if (parsed?.Text is null)
        {
            throw new ParakeetClientException("parakeet response missing 'text' field");
        }
        return new ParakeetTranscript(parsed.Text, parsed.Language);
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code >= 500 || code == HttpStatusCode.RequestTimeout || code == HttpStatusCode.TooManyRequests;

    private static string Truncate(string body) =>
        body.Length <= 512 ? body : body[..512] + "...";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "ParakeetHttpClient non-success: status={Status} storage_key={Key}")]
    private static partial void LogFailure(ILogger logger, int status, string key);

    internal sealed record ParakeetResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);
}
