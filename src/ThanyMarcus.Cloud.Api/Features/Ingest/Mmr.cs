namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class Mmr
{
    public static List<RelatedNote> Select(
        float[] query,
        IReadOnlyList<RelatedNote> candidates,
        int k,
        double lambda)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var take = Math.Min(k, candidates.Count);
        var selected = new List<RelatedNote>(take);
        if (take == 0) return selected;

        var remaining = new List<RelatedNote>(candidates);
        while (selected.Count < take && remaining.Count > 0)
        {
            var bestIdx = 0;
            var bestScore = double.NegativeInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var relevance = Dot(query, remaining[i].Embedding);
                var maxSimToSelected = selected.Count == 0
                    ? 0.0
                    : selected.Max(s => Dot(remaining[i].Embedding, s.Embedding));
                var score = (lambda * relevance) - ((1.0 - lambda) * maxSimToSelected);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }
            selected.Add(remaining[bestIdx]);
            remaining.RemoveAt(bestIdx);
        }
        return selected;
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"vector dimension mismatch: {a.Length} vs {b.Length}");
        }
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
