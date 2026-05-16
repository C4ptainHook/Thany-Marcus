using System.Net;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

public sealed class FakeCloudflareDnsClient : ICloudflareDnsClient
{
    public List<(string Subdomain, IPAddress Ip)> Creates { get; } = [];
    public List<string> Deletes { get; } = [];

    public bool ThrowOnCreate { get; set; }
    public bool ThrowOnDelete { get; set; }

    public Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, CancellationToken ct)
    {
        _ = cloudflareToken;
        if (ThrowOnCreate)
        {
            throw new InvalidOperationException("cloudflare create failed (fake)");
        }
        Creates.Add((subdomain, ip));
        return Task.FromResult(new DnsRecord("rec-" + Guid.NewGuid().ToString("N"), subdomain, ip));
    }

    public Task DeleteAsync(string recordId, string cloudflareToken, CancellationToken ct)
    {
        _ = cloudflareToken;
        Deletes.Add(recordId);
        if (ThrowOnDelete)
        {
            throw new InvalidOperationException("cloudflare delete failed (fake)");
        }
        return Task.CompletedTask;
    }
}
