using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public const int Dimensions = 256;

    private readonly Func<string, float[]>? generator;
    public int CallCount { get; private set; }
    public List<string> Inputs { get; } = new();

    public FakeEmbeddingClient(Func<string, float[]>? generator = null)
    {
        this.generator = generator;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        CallCount++;
        Inputs.Add(text ?? "");
        var vec = generator is null ? new float[Dimensions] : generator(text ?? "");
        return Task.FromResult(vec);
    }

    public static float[] DeterministicUnitVector(string text)
    {
        var v = new float[Dimensions];
        var seed = 0u;
        foreach (var c in text ?? "") seed = unchecked(seed * 16777619u ^ c);
        if (seed == 0) seed = 1;
        var rng = new Random(unchecked((int)seed));
        double sum = 0;
        for (var i = 0; i < Dimensions; i++)
        {
            var x = (float)(rng.NextDouble() * 2.0 - 1.0);
            v[i] = x;
            sum += x * x;
        }
        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            var inv = (float)(1.0 / norm);
            for (var i = 0; i < Dimensions; i++) v[i] *= inv;
        }
        return v;
    }
}
