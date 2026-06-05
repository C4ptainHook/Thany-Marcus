using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

public sealed class MmrTests
{
    private static readonly float[] Query = Normalize(1f, 0f, 0f);

    // A and B are an identical near-duplicate pair, both more relevant to the query
    // than C. C points in a different direction (less relevant, but diverse).
    private static readonly RelatedNote A = Candidate("A", Normalize(1f, 0.3f, 0f));
    private static readonly RelatedNote B = Candidate("B", Normalize(1f, 0.3f, 0f));
    private static readonly RelatedNote C = Candidate("C", Normalize(1f, 0f, 0.4f));

    [Fact]
    public void Diverse_pick_beats_pure_topk_on_near_duplicate_set()
    {
        var picked = Mmr.Select(Query, new[] { A, B, C }, k: 2, lambda: 0.6);

        picked.Count.ShouldBe(2);
        picked[0].RelativePath.ShouldBe("A");
        // Diversity wins the second slot for C over the near-duplicate B.
        picked[1].RelativePath.ShouldBe("C");
    }

    [Fact]
    public void Lambda_one_reduces_to_pure_topk()
    {
        var picked = Mmr.Select(Query, new[] { A, B, C }, k: 2, lambda: 1.0);

        picked.Count.ShouldBe(2);
        picked[0].RelativePath.ShouldBe("A");
        // Pure relevance keeps the near-duplicate B (it out-scores the less-relevant C).
        picked[1].RelativePath.ShouldBe("B");
    }

    [Fact]
    public void Opposed_item_is_rewarded_over_orthogonal_item()
    {
        // First slot goes to the most relevant, off-axis anchor.
        var anchor = Candidate("anchor", Normalize(0.8f, 0.6f, 0f));
        // O is orthogonal to the anchor but slightly more relevant to the query than P.
        var orthogonal = Candidate("orthogonal", Normalize(0.55f, -0.7333f, 0.3996f));
        // P is anti-correlated with the anchor (negative cosine) — true diversity.
        var opposed = Candidate("opposed", Normalize(0.5f, -0.8f, 0.3317f));

        var picked = Mmr.Select(Query, new[] { anchor, orthogonal, opposed }, k: 2, lambda: 0.4);

        picked[0].RelativePath.ShouldBe("anchor");
        // With the diversity penalty floored at 0 the orthogonal item would win;
        // crediting the negative similarity lets the opposed item take the slot.
        picked[1].RelativePath.ShouldBe("opposed");
    }

    [Fact]
    public void Dimension_mismatch_throws()
    {
        var bad = new RelatedNote(Guid.NewGuid(), "bad", "body", new float[128], Distance: 0.0);
        Should.Throw<ArgumentException>(
            () => Mmr.Select(Query, new[] { bad }, k: 1, lambda: 0.6));
    }

    [Fact]
    public void K_larger_than_candidate_count_returns_all()
    {
        var picked = Mmr.Select(Query, new[] { A, C }, k: 10, lambda: 0.6);
        picked.Count.ShouldBe(2);
    }

    [Fact]
    public void Empty_candidates_returns_empty()
    {
        var picked = Mmr.Select(Query, Array.Empty<RelatedNote>(), k: 5, lambda: 0.6);
        picked.ShouldBeEmpty();
    }

    private static RelatedNote Candidate(string path, float[] embedding)
    {
        double dot = 0;
        for (var i = 0; i < embedding.Length; i++) dot += (double)embedding[i] * Query[i];
        return new RelatedNote(
            Id: Guid.NewGuid(),
            RelativePath: path,
            BodyOutput: $"# {path}\n\nbody",
            Embedding: embedding,
            Distance: 1.0 - dot);
    }

    private static float[] Normalize(params float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        var inv = (float)(1.0 / Math.Sqrt(sum));
        var outv = new float[v.Length];
        for (var i = 0; i < v.Length; i++) outv[i] = v[i] * inv;
        return outv;
    }
}
