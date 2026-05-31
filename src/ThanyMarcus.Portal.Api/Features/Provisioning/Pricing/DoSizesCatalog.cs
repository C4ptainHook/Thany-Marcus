using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;

public interface IDoSizesCatalog
{
    Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct);
}

public sealed record DoSize(
    string Slug,
    decimal MonthlyUsd,
    decimal HourlyUsd,
    int Memory,
    int Vcpus,
    int Disk);

public sealed partial class DoSizesCatalog(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<DoSizesCatalog> log) : IDoSizesCatalog
{
    public const string HttpClientName = "do-sizes";
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const string CacheKey = "do:sizes:v1";

    public async Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(slug);

        if (cache.TryGetValue<IReadOnlyDictionary<string, DoSize>>(CacheKey, out var cached) && cached is not null)
            return cached.TryGetValue(slug, out var hit) ? hit : null;

        var fresh = await FetchAsync(oauthToken, ct);
        if (fresh is null) return null;

        cache.Set(CacheKey, fresh, Ttl);
        return fresh.TryGetValue(slug, out var size) ? size : null;
    }

    private async Task<IReadOnlyDictionary<string, DoSize>?> FetchAsync(string oauthToken, CancellationToken ct)
    {
        try
        {
            using var http = httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, "v2/sizes?per_page=200");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogFetchHttpError(log, (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("sizes", out var sizesEl)) return null;
            if (sizesEl.ValueKind != JsonValueKind.Array) return null;

            var map = new Dictionary<string, DoSize>(StringComparer.Ordinal);
            foreach (var el in sizesEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var slug = el.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                if (string.IsNullOrEmpty(slug)) continue;

                var monthly = ReadDecimal(el, "price_monthly");
                var hourly  = ReadDecimal(el, "price_hourly");
                var memory  = ReadInt(el, "memory");
                var vcpus   = ReadInt(el, "vcpus");
                var disk    = ReadInt(el, "disk");

                if (monthly is null || hourly is null) continue;

                map[slug] = new DoSize(slug, monthly.Value, hourly.Value, memory ?? 0, vcpus ?? 0, disk ?? 0);
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFetchException(log, ex);
            return null;
        }
    }

    private static decimal? ReadDecimal(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal()
            : null;

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DoSizesCatalog: /v2/sizes returned non-success status {StatusCode}; pricing not cached")]
    private static partial void LogFetchHttpError(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "DoSizesCatalog: failed to fetch /v2/sizes; pricing not cached")]
    private static partial void LogFetchException(ILogger logger, Exception ex);
}
