using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class HostnameGenerator(PortalDbContext db, IRandomHexProvider random)
{
    private const string Suffix = ".thany.click";
    private const int MaxAttempts = 5;

    public async Task<string> GenerateAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var candidate = random.GetHex(8).ToLowerInvariant() + Suffix;
            var taken = await db.Clouds
                .IgnoreQueryFilters()
                .AnyAsync(c => c.Hostname == candidate, ct);
            if (!taken) return candidate;
        }
        throw new InvalidOperationException("hostname_generation_exhausted");
    }
}

public interface IRandomHexProvider
{
    string GetHex(int byteCount);
}

public sealed class CryptoRandomHexProvider : IRandomHexProvider
{
    public string GetHex(int byteCount) =>
        RandomNumberGenerator.GetHexString(byteCount * 2);
}
