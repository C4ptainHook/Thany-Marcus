using Microsoft.EntityFrameworkCore;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

namespace ThanyMarcus.Cloud.Tests.Features.EntitySuggestions;

[Collection(PostgresCollection.Name)]
public sealed class EntitySuggestionAggregatorTests(PostgresFixture postgres)
{
    private static readonly string[] NoAliases = Array.Empty<string>();
    private static readonly StaticOptionsMonitor<LlmIntelligenceOptions> Opts = new(new LlmIntelligenceOptions());

    [Fact]
    public async Task First_observation_creates_a_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var note1 = Guid.CreateVersion7();

        await RunAsync("Michael Jackson", EntityKind.Person, note1, Vec("Michael Jackson"), ct);

        using var probe = NewDb();
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].CanonicalText.ShouldBe("Michael Jackson");
        rows[0].OccurrenceCount.ShouldBe(1);
        rows[0].DistinctNoteCount.ShouldBe(1);
    }

    [Fact]
    public async Task Same_anchor_in_second_note_increments_count_and_distinct_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var note1 = Guid.CreateVersion7();
        var note2 = Guid.CreateVersion7();

        await RunAsync("Michael Jackson", EntityKind.Person, note1, Vec("Michael Jackson"), ct);
        await RunAsync("Michael Jackson", EntityKind.Person, note2, Vec("Michael Jackson"), ct);

        using var probe = NewDb();
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].OccurrenceCount.ShouldBe(2);
        rows[0].DistinctNoteCount.ShouldBe(2);
    }

    [Fact]
    public async Task Same_note_repeated_does_not_increment_distinct_count()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var note1 = Guid.CreateVersion7();

        // Two mentions of the same canonical within ONE run (one note) — the in-run cache folds them.
        await RunTwiceSameNoteAsync(note1, ct);

        using var probe = NewDb();
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].OccurrenceCount.ShouldBe(2);
        rows[0].DistinctNoteCount.ShouldBe(1);
    }

    [Fact]
    public async Task KnnNear_anchor_in_third_note_appends_alias_not_a_new_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var note1 = Guid.CreateVersion7();
        var note2 = Guid.CreateVersion7();
        var note3 = Guid.CreateVersion7();

        await RunAsync("Michael Jackson", EntityKind.Person, note1, Vec("Michael Jackson"), ct);
        await RunAsync("Michael Jackson", EntityKind.Person, note2, Vec("Michael Jackson"), ct);
        // A different surface form whose embedding is near the existing suggestion → folds in as an alias.
        await RunAsync("Mike", EntityKind.Person, note3, Vec("Michael Jackson"), ct);

        using var probe = NewDb();
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].CanonicalText.ShouldBe("Michael Jackson");
        rows[0].OccurrenceCount.ShouldBe(3);
        rows[0].DistinctNoteCount.ShouldBe(3);
        rows[0].Aliases.ShouldContain("Mike");
    }

    [Fact]
    public async Task Distant_candidate_of_same_kind_creates_a_separate_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var note1 = Guid.CreateVersion7();
        var note2 = Guid.CreateVersion7();

        await RunAsync("Michael Jackson", EntityKind.Person, note1, Vec("Michael Jackson"), ct);
        await RunAsync("Quincy Jones", EntityKind.Person, note2, Vec("Quincy Jones"), ct);

        using var probe = NewDb();
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Count.ShouldBe(2);
    }

    private async Task RunAsync(string canonical, string kind, Guid noteId, Vector embedding, CancellationToken ct)
    {
        using var db = NewDb();
        var agg = new EntitySuggestionAggregator(db, Opts);
        var now = SystemClock.Instance.GetCurrentInstant();
        await agg.AppendOrCreateAsync(Candidate(canonical, kind), noteId, "surrounding", embedding, now, null, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task RunTwiceSameNoteAsync(Guid noteId, CancellationToken ct)
    {
        using var db = NewDb();
        var agg = new EntitySuggestionAggregator(db, Opts);
        var now = SystemClock.Instance.GetCurrentInstant();
        var cand = Candidate("Michael Jackson", EntityKind.Person);
        await agg.AppendOrCreateAsync(cand, noteId, "ctx a", Vec("Michael Jackson"), now, null, ct);
        await agg.AppendOrCreateAsync(cand, noteId, "ctx b", Vec("Michael Jackson"), now, null, ct);
        await db.SaveChangesAsync(ct);
    }

    private static MentionCandidateDto Candidate(string canonical, string kind) =>
        new(AnchorText: canonical, StartOffset: 0, EndOffset: canonical.Length,
            CandidateKind: kind, CandidateCanonical: canonical, Aliases: NoAliases, Confidence: 0.9);

    private static Vector Vec(string text) => new(FakeEmbeddingClient.DeterministicUnitVector(text));

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
