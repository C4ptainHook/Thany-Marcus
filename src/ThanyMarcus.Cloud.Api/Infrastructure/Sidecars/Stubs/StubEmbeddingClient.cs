namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubEmbeddingClient : IEmbeddingClient
{
    public const int Dimensions = 256;

    // stub returns zero vector; real Granite ONNX lands in CLOUD-EMBEDDING (handoff #5)
    public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        Task.FromResult(new float[Dimensions]);
}
