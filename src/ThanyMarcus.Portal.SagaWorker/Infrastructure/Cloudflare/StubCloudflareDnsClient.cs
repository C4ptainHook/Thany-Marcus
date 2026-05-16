using System.Net;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

public sealed partial class StubCloudflareDnsClient(ILogger<StubCloudflareDnsClient> log) : ICloudflareDnsClient
{
    public Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, CancellationToken ct)
    {
        _ = cloudflareToken;
        var record = new DnsRecord("stub-" + Guid.NewGuid().ToString("N"), subdomain, ip);
        LogCreate(log, subdomain, ip);
        return Task.FromResult(record);
    }

    public Task DeleteAsync(string recordId, string cloudflareToken, CancellationToken ct)
    {
        _ = cloudflareToken;
        LogDelete(log, recordId);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "StubCloudflareDnsClient.CreateA: {Subdomain} -> {Ip}")]
    private static partial void LogCreate(ILogger logger, string subdomain, IPAddress ip);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "StubCloudflareDnsClient.Delete: {RecordId}")]
    private static partial void LogDelete(ILogger logger, string recordId);
}
