namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
