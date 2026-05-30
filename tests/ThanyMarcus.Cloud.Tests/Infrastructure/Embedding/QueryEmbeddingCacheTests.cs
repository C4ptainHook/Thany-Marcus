using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

public sealed class QueryEmbeddingCacheTests
{
    [Fact]
    public async Task Miss_invokes_client_once_and_caches_result()
    {
        var client = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        using var cache = NewCache();

        var first = await cache.GetOrAddAsync("hello world body", client, CancellationToken.None);
        var second = await cache.GetOrAddAsync("hello world body", client, CancellationToken.None);

        client.CallCount.ShouldBe(1);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task Distinct_bodies_each_invoke_client()
    {
        var client = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        using var cache = NewCache();

        await cache.GetOrAddAsync("body one", client, CancellationToken.None);
        await cache.GetOrAddAsync("body two", client, CancellationToken.None);

        client.CallCount.ShouldBe(2);
    }

    private static QueryEmbeddingCache NewCache() =>
        new(Options.Create(new QueryEmbeddingCacheOptions { TtlSeconds = 90, MaxEntries = 500 }));
}
