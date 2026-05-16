using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public sealed partial class TurnstileValidator(
    HttpClient http,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileValidator> logger)
    : ITurnstileValidator
{
    private static readonly Meter Meter = new("ThanyMarcus.Portal.Auth.Turnstile");
    private static readonly Counter<long> SiteVerifyCounter = Meter.CreateCounter<long>(
        "portal_turnstile_siteverify_total",
        description: "Cloudflare Turnstile siteverify calls, by outcome.");

    public async Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
    {
        var cfg = options.Value;
        if (!cfg.IsEnabled) return TurnstileVerifyResult.Disabled;
        if (string.IsNullOrWhiteSpace(token)) return TurnstileVerifyResult.Empty;

        var form = new Dictionary<string, string>
        {
            ["secret"]   = cfg.SecretKey,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp)) form["remoteip"] = remoteIp;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            using var resp = await http.PostAsync(
                new Uri(cfg.SiteVerifyUrl, UriKind.Absolute),
                new FormUrlEncodedContent(form),
                cts.Token);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadFromJsonAsync<SiteVerifyResponse>(cts.Token)
                ?? new SiteVerifyResponse(false, ["empty-response"]);
            var result = new TurnstileVerifyResult(payload.Success, payload.ErrorCodes ?? []);
            SiteVerifyCounter.Add(1, new KeyValuePair<string, object?>("outcome", result.Success ? "pass" : "fail"));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            LogSiteVerifyFailed(logger, ex);
            SiteVerifyCounter.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
            return new TurnstileVerifyResult(true, ["transport-error"]);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Turnstile siteverify call failed; failing open")]
    private static partial void LogSiteVerifyFailed(ILogger logger, Exception ex);

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] IReadOnlyList<string>? ErrorCodes);
}
