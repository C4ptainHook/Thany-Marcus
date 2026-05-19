using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed partial class DigitalOceanOAuthClient(
    HttpClient http,
    IOptions<DigitalOceanOAuthOptions> options,
    ILogger<DigitalOceanOAuthClient> log) : IDigitalOceanOAuthClient
{
    public const string HttpClientName = "DigitalOceanOAuth";

    private readonly DigitalOceanOAuthOptions opts = options.Value;

    public async Task<DoTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["client_id"]     = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
            ["redirect_uri"]  = opts.CallbackUrl,
        });
        return await PostTokenAsync(opts.TokenEndpoint, form, ct);
    }

    public async Task<DoTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"]     = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
        });
        using var resp = await http.PostAsync(opts.TokenEndpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new DigitalOceanOAuthRefreshFailedException(
                $"refresh failed: {await SafeReadAsync(resp, ct)}", (int)resp.StatusCode);
        }
        return await ReadTokenAsync(resp, ct);
    }

    public async Task RevokeOAuthTokenAsync(string accessToken, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = accessToken,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, opts.RevokeEndpoint) { Content = form };
        SetBasicAuth(req);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
        {
            LogRevokeNonSuccess(log, (int)resp.StatusCode);
        }
    }

    public async Task<DoSpacesKeyMint> MintSpacesFullAccessKeyAsync(string accessToken, string name, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"]   = name,
            ["grants"] = new JsonArray
            {
                new JsonObject
                {
                    ["bucket"]     = "",
                    ["permission"] = "fullaccess",
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(opts.ApiBaseUrl), "v2/spaces/keys"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(body);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new DigitalOceanOAuthException(
                $"mint spaces key failed: {await SafeReadAsync(resp, ct)}", (int)resp.StatusCode);
        }
        var envelope = await resp.Content.ReadFromJsonAsync<DoSpacesKeyResponseEnvelope>(ct)
            ?? throw new DigitalOceanOAuthException("mint spaces key: empty body", (int)resp.StatusCode);
        var key = envelope.Key
            ?? throw new DigitalOceanOAuthException("mint spaces key: missing 'key' in response", (int)resp.StatusCode);
        if (string.IsNullOrEmpty(key.AccessKey) || string.IsNullOrEmpty(key.SecretKey))
            throw new DigitalOceanOAuthException("mint spaces key: empty access/secret in response", (int)resp.StatusCode);

        return new DoSpacesKeyMint { AccessKeyId = key.AccessKey, SecretKey = key.SecretKey };
    }

    public async Task DeleteSpacesKeyAsync(string accessToken, string accessKeyId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            new Uri(new Uri(opts.ApiBaseUrl), $"v2/spaces/keys/{Uri.EscapeDataString(accessKeyId)}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
        {
            LogDeleteSpacesKeyNonSuccess(log, (int)resp.StatusCode);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DigitalOcean OAuth revoke returned {StatusCode}; continuing")]
    private static partial void LogRevokeNonSuccess(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "DigitalOcean delete spaces key returned {StatusCode}; continuing")]
    private static partial void LogDeleteSpacesKeyNonSuccess(ILogger logger, int statusCode);

    private async Task<DoTokenResponse> PostTokenAsync(string url, HttpContent content, CancellationToken ct)
    {
        using var resp = await http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new DigitalOceanOAuthException(
                $"token endpoint returned {(int)resp.StatusCode}: {await SafeReadAsync(resp, ct)}",
                (int)resp.StatusCode);
        }
        return await ReadTokenAsync(resp, ct);
    }

    private static async Task<DoTokenResponse> ReadTokenAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var token = await resp.Content.ReadFromJsonAsync<DoTokenResponse>(ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new DigitalOceanOAuthException("token response missing access_token", (int)resp.StatusCode);
        return token;
    }

    private void SetBasicAuth(HttpRequestMessage req)
    {
        var raw   = $"{opts.ClientId}:{opts.ClientSecret}";
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }
}
