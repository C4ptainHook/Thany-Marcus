using Microsoft.EntityFrameworkCore;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Entities;

[Collection(PostgresCollection.Name)]
public sealed class DisplayNameRecomputeTests(PostgresFixture postgres)
{
    private const int Margin = 2;

    [Fact]
    public async Task Most_frequent_form_wins_and_canonical_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        // Canonical is the Ukrainian form; the vault writes "Kyiv" far more often.
        var entityId = await SeedEntityWithMentionsAsync("Київ", ("Київ", 2), ("Kyiv", 5));

        using var db = NewDb();
        var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
        var changed = await DisplayNameRecomputer.RecomputeAsync(db, entity, Margin, ct);
        await db.SaveChangesAsync(ct);

        changed.ShouldBeTrue();
        entity.DisplayName.ShouldBe("Kyiv");
        entity.CanonicalName.ShouldBe("Київ"); // identity never moves
    }

    [Fact]
    public async Task Hysteresis_prevents_flip_on_a_one_count_lead()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        // Challenger leads by only 1 (4 vs 3) — below the +2 margin, so the label holds.
        var entityId = await SeedEntityWithMentionsAsync("Київ", ("Київ", 3), ("Kyiv", 4));

        using var db = NewDb();
        var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
        var changed = await DisplayNameRecomputer.RecomputeAsync(db, entity, Margin, ct);

        changed.ShouldBeFalse();
        entity.DisplayName.ShouldBeNull();
        entity.CanonicalName.ShouldBe("Київ");
    }

    [Fact]
    public async Task No_mentions_leaves_display_name_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var entityId = await SeedEntityWithMentionsAsync("Київ");

        using var db = NewDb();
        var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
        (await DisplayNameRecomputer.RecomputeAsync(db, entity, Margin, ct)).ShouldBeFalse();
        entity.DisplayName.ShouldBeNull();
    }

    private async Task<Guid> SeedEntityWithMentionsAsync(string canonical, params (string anchor, int count)[] forms)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();

        var entityId = Guid.CreateVersion7();
        db.Entities.Add(new Entity
        {
            Id = entityId,
            Kind = EntityKind.Place,
            CanonicalName = canonical,
            Source = EntitySource.User,
            Embedding = new Vector(new float[256]),
            CreatedAt = now,
            UpdatedAt = now,
        });

        var noteId = Guid.CreateVersion7();
        db.Notes.Add(new Note
        {
            Id = noteId,
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "x",
            BodyOutput = "x",
            RelativePath = $"Inbox/{noteId}.md",
            CreatedAt = now,
            UpdatedAt = now,
        });

        foreach (var (anchor, count) in forms)
        {
            for (var i = 0; i < count; i++)
            {
                db.Mentions.Add(new Mention
                {
                    Id = Guid.CreateVersion7(),
                    EntityId = entityId,
                    NoteId = noteId,
                    AnchorText = anchor,
                    StartOffset = 0,
                    EndOffset = anchor.Length,
                    Confidence = 0.9f,
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync();
        return entityId;
    }

    private CloudDbContext NewDb() => JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
}
