using System.Globalization;
using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public sealed record OEmbedResponse(
    string? Title,
    string? AuthorName,
    string? AuthorUrl,
    string? ProviderName,
    string? ThumbnailUrl,
    string? Type,
    string? Html);

public static class OEmbedClient
{
    public const int MaxOEmbedBytes = 64 * 1024;
    public static readonly TimeSpan OEmbedTimeout = TimeSpan.FromSeconds(5);

    public static async Task<OEmbedResponse?> FetchAsync(
        HttpClient client,
        Uri href,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OEmbedTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, href);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!media.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream(capacity: 8 * 1024);
        var buffer = new byte[8 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxOEmbedBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return Parse(ms.ToArray());
    }

    public static OEmbedResponse? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            return new OEmbedResponse(
                Title: GetString(root, "title"),
                AuthorName: GetString(root, "author_name"),
                AuthorUrl: GetString(root, "author_url"),
                ProviderName: GetString(root, "provider_name"),
                ThumbnailUrl: GetString(root, "thumbnail_url"),
                Type: GetString(root, "type"),
                Html: GetString(root, "html"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => el.GetBoolean().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
