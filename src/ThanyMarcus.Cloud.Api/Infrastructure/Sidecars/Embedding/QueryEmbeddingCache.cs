using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

public sealed class QueryEmbeddingCacheOptions
{
    public int TtlSeconds { get; set; } = 90;
    public int MaxEntries { get; set; } = 500;
}

public sealed class QueryEmbeddingCache : IDisposable
{
    private readonly MemoryCache cache;
    private readonly TimeSpan ttl;

    public QueryEmbeddingCache(IOptions<QueryEmbeddingCacheOptions> opts)
    {
        var o = opts.Value;
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = o.MaxEntries });
        ttl = TimeSpan.FromSeconds(o.TtlSeconds);
    }

    public async Task<float[]> GetOrAddAsync(
        string body,
        IEmbeddingClient client,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(client);

        var key = HashKey(body);
        if (cache.TryGetValue<float[]>(key, out var hit) && hit is not null)
        {
            return hit;
        }
        var vec = await client.EmbedAsync(body, ct);
        cache.Set(key, vec, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1,
        });
        return vec;
    }

    internal static string HashKey(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes);
    }

    public void Dispose() => cache.Dispose();
}
