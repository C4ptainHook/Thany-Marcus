using System.Net.Http.Headers;
using System.Net.Http.Json;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;

public sealed class PortalToCloudPluginTokenClient(IHttpClientFactory httpFactory) : IPortalToCloudPluginTokenClient
{
    public const string HttpClientName = "PluginTokenSyncClient";

    public async Task<Guid> PostAsync(
        string cloudUrl,
        string cloudAdminToken,
        byte[] tokenHashBytes,
        string label,
        CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, cloudUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cloudAdminToken);
        req.Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
            TokenHashBase64: Convert.ToBase64String(tokenHashBytes),
            Label: label));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginTokenSyncException($"transport error: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new PluginTokenSyncException("timeout", inner: ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new PluginTokenSyncException(
                    $"cloud returned {(int)resp.StatusCode}",
                    statusCode: (int)resp.StatusCode);
            }

            var body = await resp.Content.ReadFromJsonAsync<AdminIssuePluginTokenResponse>(ct);
            if (body is null)
            {
                throw new PluginTokenSyncException("missing response body");
            }
            return body.TokenId;
        }
    }

    public async Task RevokeAsync(
        string cloudUrl,
        string cloudAdminToken,
        byte[] tokenHashBytes,
        CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, cloudUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cloudAdminToken);
        req.Content = JsonContent.Create(new AdminRevokePluginTokenRequest(
            TokenHashBase64: Convert.ToBase64String(tokenHashBytes)));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginTokenSyncException($"transport error: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new PluginTokenSyncException("timeout", inner: ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new PluginTokenSyncException(
                    $"cloud returned {(int)resp.StatusCode}",
                    statusCode: (int)resp.StatusCode);
            }
        }
    }
}
