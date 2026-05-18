using System.Globalization;
using ReverseMarkdown;
using SmartReader;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public sealed class UrlExtractor : IUrlExtractor
{
    public const int MaxResponseBytes  = 5 * 1024 * 1024;
    public const int MaxMarkdownBytes  = 500 * 1024;
    public const string UserAgent      = "Thany-Marcus/1.0 (+https://thany.click)";
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    public const string TruncationSentinel = "\n\n[…truncated]";

    private readonly IHttpClientFactory clientFactory;
    private readonly ILogger<UrlExtractor> log;

    public UrlExtractor(IHttpClientFactory clientFactory, ILogger<UrlExtractor> log)
    {
        this.clientFactory = clientFactory;
        this.log = log;
    }

    public const string HttpClientName = "UrlExtractor";

    public async Task<UrlExtractionResult> ExtractAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid URL: {url}");
        }

        using var client = clientFactory.CreateClient(HttpClientName);
        client.Timeout = FetchTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} fetching {uri}");
        }

        var contentLength = resp.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"Content-Length {contentLength.Value} exceeds cap {MaxResponseBytes}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var html = await ReadCappedAsync(stream, MaxResponseBytes, ct).ConfigureAwait(false);

        var reader = new Reader(uri.ToString(), html);
        var article = reader.GetArticle();

        if (!article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
        {
            throw new InvalidOperationException(
                $"SmartReader could not extract readable content from {uri}");
        }

        var converter = new Converter(new ReverseMarkdown.Config
        {
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
            RemoveComments = true,
            SmartHrefHandling = true,
            GithubFlavored = true,
        });

        var markdown = converter.Convert(article.Content);
        var truncated = false;
        if (markdown.Length > MaxMarkdownBytes)
        {
            markdown = markdown[..MaxMarkdownBytes] + TruncationSentinel;
            truncated = true;
        }

        return new UrlExtractionResult(
            Markdown:     markdown,
            CanonicalUrl: article.Uri?.ToString() ?? uri.ToString(),
            Title:        article.Title,
            HttpStatus:   (int)resp.StatusCode,
            Truncated:    truncated);
    }

    private static async Task<string> ReadCappedAsync(Stream stream, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream(capacity: Math.Min(cap, 64 * 1024));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > cap)
            {
                throw new InvalidOperationException(
                    $"Response exceeded {cap.ToString(CultureInfo.InvariantCulture)} bytes");
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
