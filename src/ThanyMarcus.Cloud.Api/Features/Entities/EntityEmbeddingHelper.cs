using Pgvector;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

internal static class EntityEmbeddingHelper
{
    public static async Task<Vector> EmbedCanonicalAsync(
        IEmbeddingClient client, string canonicalName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var vec = await client.EmbedAsync(canonicalName ?? "", ct).ConfigureAwait(false);
        return new Vector(vec);
    }
}
