using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

[Trait("Category", "Slow")]
public sealed class GraniteEmbeddingClientTests : IClassFixture<GraniteEmbeddingFixture>
{
    private readonly GraniteEmbeddingFixture fixture;

    public GraniteEmbeddingClientTests(GraniteEmbeddingFixture fixture)
    {
        this.fixture = fixture;
    }

    private GraniteEmbeddingClient Client()
    {
        Assert.SkipUnless(fixture.ModelAvailable, fixture.SkipReason ?? "Granite model not available");
        return fixture.Client!;
    }

    [Fact]
    public async Task EmbedAsync_returns_256_dim_unit_vector()
    {
        var client = Client();
        var ct = TestContext.Current.CancellationToken;
        var v = await client.EmbedAsync("hello world", ct);
        v.Length.ShouldBe(256);
        L2(v).ShouldBe(1.0, tolerance: 1e-3);
    }

    [Fact]
    public async Task Related_pair_has_higher_cosine_than_unrelated_pair()
    {
        var client = Client();
        var ct = TestContext.Current.CancellationToken;
        var a = await client.EmbedAsync("cat sat on the mat", ct);
        var b = await client.EmbedAsync("feline rested on a rug", ct);
        var c = await client.EmbedAsync("quantum mechanics is hard", ct);
        Cosine(a, b).ShouldBeGreaterThan(Cosine(a, c));
    }

    [Fact]
    public async Task Cross_lingual_en_ru_pair_is_more_similar_than_unrelated_en_pair()
    {
        var client = Client();
        var ct = TestContext.Current.CancellationToken;
        var en = await client.EmbedAsync("hello world", ct);
        var ru = await client.EmbedAsync("привет мир", ct);
        var unrelated = await client.EmbedAsync("banana smoothie recipe", ct);
        Cosine(en, ru).ShouldBeGreaterThan(Cosine(en, unrelated));
    }

    [Fact]
    public async Task Empty_input_does_not_throw_and_returns_256_dim()
    {
        var client = Client();
        var ct = TestContext.Current.CancellationToken;
        var v = await client.EmbedAsync("", ct);
        v.Length.ShouldBe(256);
    }

    [Fact]
    public async Task Long_input_truncates_at_max_tokens_without_throwing()
    {
        var client = Client();
        var ct = TestContext.Current.CancellationToken;
        var text = string.Join(' ', Enumerable.Repeat("token", 4000));
        var v = await client.EmbedAsync(text, ct);
        v.Length.ShouldBe(256);
        L2(v).ShouldBe(1.0, tolerance: 1e-3);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    private static double L2(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        return Math.Sqrt(sum);
    }
}
