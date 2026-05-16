using System.Net;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

public interface ICloudflareDnsClient
{
    Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, CancellationToken ct);
    Task DeleteAsync(string recordId, string cloudflareToken, CancellationToken ct);
}

public sealed record DnsRecord(string Id, string Subdomain, IPAddress Ip);
