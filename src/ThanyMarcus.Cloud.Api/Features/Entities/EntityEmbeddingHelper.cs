using System.Text;
using Pgvector;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

// One embedding recipe shared by resolution candidates and stored entities, so cosine distance is
// measured in a single space. The name carries identity; the surrounding context carries the
// cross-lingual signal a bare name lacks (Київ vs Kyiv resolve far apart on names alone) and
// disambiguates two same-language homonyms — raising cross-language recall while tightening
// same-language precision.
internal static class EntityEmbeddingHelper
{
    public static async Task<Vector> EmbedCandidateAsync(
        IEmbeddingClient client, string canonical, string? context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var text = Compose(canonical, description: null,
            contexts: string.IsNullOrWhiteSpace(context) ? [] : [context]);
        var vec = await client.EmbedAsync(text, ct).ConfigureAwait(false);
        return new Vector(vec);
    }

    public static async Task<Vector> EmbedEntityAsync(
        IEmbeddingClient client,
        string canonical,
        string? description,
        IReadOnlyList<string> contexts,
        int maxContexts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(contexts);
        var text = Compose(canonical, description, contexts.Take(Math.Max(0, maxContexts)).ToArray());
        var vec = await client.EmbedAsync(text, ct).ConfigureAwait(false);
        return new Vector(vec);
    }

    internal static string Compose(string? canonical, string? description, IReadOnlyList<string> contexts)
    {
        var sb = new StringBuilder();
        sb.Append((canonical ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append('\n').Append(description.Trim());
        }
        foreach (var c in contexts)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            sb.Append('\n').Append(c.Trim());
        }
        return sb.ToString();
    }
}
