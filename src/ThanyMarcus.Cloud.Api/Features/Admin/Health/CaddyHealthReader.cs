using System.Net.Http.Json;
using ThanyMarcus.Cloud.Api.Infrastructure.Caddy;

namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public sealed partial class CaddyHealthReader(
    CaddyAdminClient caddy,
    ILogger<CaddyHealthReader> log)
{
    public async Task<bool> IsCertReadyAsync(string hostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return false;

        try
        {
            using var resp = await caddy.GetAsync($"/pki/ca/local/active-cert/{hostname}", ct)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return true;

            using var listResp = await caddy.GetAsync("/config/apps/tls/certificates/automate", ct)
                .ConfigureAwait(false);
            if (!listResp.IsSuccessStatusCode) return false;

            var domains = await listResp.Content
                .ReadFromJsonAsync<string[]>(cancellationToken: ct)
                .ConfigureAwait(false);
            return domains is not null && Array.Exists(domains,
                d => string.Equals(d, hostname, StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(log, ex);
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            LogTimeout(log);
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Caddy admin API unreachable; assuming cert not ready")]
    private static partial void LogUnreachable(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Caddy admin API timed out; assuming cert not ready")]
    private static partial void LogTimeout(ILogger logger);
}
