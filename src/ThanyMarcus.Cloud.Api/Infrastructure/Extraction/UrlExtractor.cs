using System.Globalization;
using System.Net.Sockets;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public sealed partial class UrlExtractor : IUrlExtractor
{
    public const int MaxResponseBytes  = 5 * 1024 * 1024;
    public const int MaxHeadBytes      = 64 * 1024;
    public const string UserAgent      = "Thany-Marcus/1.0 (+https://thany.click)";
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    public const string HttpClientName = "UrlExtractor";

    private readonly IHttpClientFactory clientFactory;
    private readonly ILogger<UrlExtractor> log;

    public UrlExtractor(IHttpClientFactory clientFactory, ILogger<UrlExtractor> log)
    {
        this.clientFactory = clientFactory;
        this.log = log;
    }

    public async Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid URL: {url}");
        }

        using var client = clientFactory.CreateClient(HttpClientName);
        client.Timeout = FetchTimeout;
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return UrlExtractionResult.Minimal(uri, $"unreachable: {ShortReason(ex)}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return UrlExtractionResult.Minimal(uri, "unreachable: timeout");
        }

        try
        {
            if (!resp.IsSuccessStatusCode)
            {
                return UrlExtractionResult.Minimal(uri,
                    $"http: {((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)}");
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (string.IsNullOrEmpty(mediaType) ||
                !(mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                  mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            {
                return UrlExtractionResult.Minimal(uri, $"content-type: {mediaType}");
            }

            var finalUri = resp.RequestMessage?.RequestUri ?? uri;
            var html = await ReadCappedAsync(resp, MaxResponseBytes, ct).ConfigureAwait(false);
            var head = HeadParser.Parse(html, finalUri);
            var httpStatus = (int)resp.StatusCode;

            if (!string.IsNullOrWhiteSpace(head.OEmbedHref) &&
                Uri.TryCreate(head.OEmbedHref, UriKind.Absolute, out var oembedUri))
            {
                try
                {
                    var oembed = await OEmbedClient.FetchAsync(client, oembedUri, ct).ConfigureAwait(false);
                    if (oembed is not null)
                    {
                        return UrlExtractionResult.FromOEmbed(finalUri, head, oembed, httpStatus);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    LogOEmbedFailed(log, oembedUri.Host, ex);
                }
            }

            return UrlExtractionResult.FromMetaHead(finalUri, head, httpStatus);
        }
        finally
        {
            resp.Dispose();
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, int cap, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(capacity: Math.Min(cap, 64 * 1024));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var allowed = Math.Min(read, cap - (int)ms.Length);
            if (allowed <= 0) break;
            await ms.WriteAsync(buffer.AsMemory(0, allowed), ct).ConfigureAwait(false);
            if (ms.Length >= cap) break;
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ShortReason(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData => "dns",
                    SocketError.ConnectionRefused => "connection-refused",
                    SocketError.TimedOut => "timeout",
                    _ => "socket",
                };
            }
        }
        return ex is TaskCanceledException ? "timeout" : "network";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "UrlExtractor oEmbed fetch failed: host={Host}")]
    private static partial void LogOEmbedFailed(ILogger logger, string host, Exception ex);
}
