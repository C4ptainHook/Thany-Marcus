using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

public sealed partial class CloudflareDnsClient(
    IHttpClientFactory http,
    IOptions<CloudflareOptions> opts,
    ILogger<CloudflareDnsClient> log) : ICloudflareDnsClient
{
    public const string HttpClientName = "cloudflare";
    private const int RecordAlreadyExistsCode = 81057;

    public async Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        ApplyAuth(client, cloudflareToken);
        var zone = opts.Value.ZoneId;

        var payload = new
        {
            type = "A",
            name = subdomain,
            content = ip.ToString(),
            ttl = 1,
            proxied = false,
        };

        var response = await client.PostAsJsonAsync($"zones/{zone}/dns_records", payload, ct);
        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareDnsRecordDto>>(ct);
            if (envelope?.Result is null)
                throw new CloudflareApiException("Cloudflare returned success but no result body");
            return new DnsRecord(envelope.Result.Id, subdomain, ip);
        }

        var errEnvelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareDnsRecordDto>>(ct);
        if (errEnvelope?.Errors?.Any(e => e.Code == RecordAlreadyExistsCode) == true)
        {
            LogExistingRecord(log, subdomain);
            var existing = await FindByNameAsync(client, zone, subdomain, ct);
            if (existing is null)
                throw new CloudflareApiException($"81057 reported for {subdomain} but list query returned no match");
            return await UpdateIpAsync(client, zone, existing, subdomain, ip, ct);
        }

        var summary = errEnvelope?.Errors is null
            ? response.StatusCode.ToString()
            : string.Join(';', errEnvelope.Errors.Select(e => $"{e.Code}:{e.Message}"));
        throw new CloudflareApiException($"CreateA failed ({response.StatusCode}): {summary}");
    }

    private static async Task<DnsRecord> UpdateIpAsync(
        HttpClient client, string zone, CloudflareDnsRecordDto existing,
        string subdomain, IPAddress newIp, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"zones/{zone}/dns_records/{existing.Id}")
        {
            Content = JsonContent.Create(new { content = newIp.ToString() }),
        };
        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareDnsRecordDto>>(ct);
            var summary = envelope?.Errors is null
                ? response.StatusCode.ToString()
                : string.Join(';', envelope.Errors.Select(e => $"{e.Code}:{e.Message}"));
            throw new CloudflareApiException(
                $"81057 reuse: PATCH content failed ({response.StatusCode}) for record {existing.Id}: {summary}");
        }
        return new DnsRecord(existing.Id, subdomain, newIp);
    }

    public async Task DeleteAsync(string recordId, string cloudflareToken, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        ApplyAuth(client, cloudflareToken);
        var zone = opts.Value.ZoneId;
        var response = await client.DeleteAsync($"zones/{zone}/dns_records/{recordId}", ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        throw new CloudflareApiException($"Delete failed ({response.StatusCode}) for record {recordId}");
    }

    private void ApplyAuth(HttpClient client, string callerToken)
    {
        var token = !string.IsNullOrEmpty(callerToken) ? callerToken : opts.Value.ApiToken;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Cloudflare API token is not configured.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<CloudflareDnsRecordDto?> FindByNameAsync(
        HttpClient client, string zone, string name, CancellationToken ct)
    {
        var url = $"zones/{zone}/dns_records?type=A&name={Uri.EscapeDataString(name)}";
        var envelope = await client.GetFromJsonAsync<CloudflareEnvelope<List<CloudflareDnsRecordDto>>>(url, ct);
        return envelope?.Result?.FirstOrDefault();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Cloudflare A record for {Subdomain} already exists; reusing existing record")]
    private static partial void LogExistingRecord(ILogger logger, string subdomain);
}
