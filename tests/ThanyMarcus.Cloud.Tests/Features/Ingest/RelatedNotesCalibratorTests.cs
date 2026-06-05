using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class RelatedNotesCalibratorTests(PostgresFixture postgres)
{
    private static RelatedNotesOptions LowGateOptions() => new()
    {
        MaxDistance = 0.7,
        MaxDistanceFloor = 0.4,
        MaxDistanceCeiling = 0.9,
        AutoCalibrationEnabled = true,
        AutoMinNotes = 4,
        AutoMinPositivePairs = 1,
        AutoMinNegativePairs = 1,
        AutoStaleNoteDelta = 1,
        AutoStaleEntityDelta = 1,
        AutoHysteresisMargin = 0.0,
        AutoMinAuc = 0.0,
        AutoRandomSampleSize = 100,
    };

    [Fact]
    public async Task Recalibrates_from_shared_entity_graph_and_persists_auto_and_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await ClearCalibrationFieldsAsync();

        // Three notes share entity E (positives, identical vectors → distance 0); two unrelated (negatives, far).
        var cluster = FakeEmbeddingClient.DeterministicUnitVector("cluster");
        var n1 = await SeedNoteAsync(cluster);
        var n2 = await SeedNoteAsync(cluster);
        var n3 = await SeedNoteAsync(cluster);
        await SeedNoteAsync(FakeEmbeddingClient.DeterministicUnitVector("alpha"));
        await SeedNoteAsync(FakeEmbeddingClient.DeterministicUnitVector("beta"));
        await SeedEntityWithMentionsAsync(n1, n2, n3);

        var o = LowGateOptions();
        CalibrationStatus status;
        using (var db = NewDb(postgres.ConnectionString))
        {
            var calibrator = new RelatedNotesCalibrator(
                db, Options.Create(o), SystemClock.Instance, NullLogger<RelatedNotesCalibrator>.Instance);
            status = await calibrator.RunAsync(ct);
        }

        status.ShouldBe(CalibrationStatus.Recalibrated);

        using (var probe = NewDb(postgres.ConnectionString))
        {
            var s = await probe.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId, ct);
            s.RelatedNotesMaxDistanceAuto.ShouldNotBeNull();
            s.RelatedNotesMaxDistanceAuto!.Value.ShouldBeInRange(o.MaxDistanceFloor, o.MaxDistanceCeiling);
            s.RelatedNotesAutoNoteCount.ShouldBe(5);
            s.RelatedNotesAutoEntityCount.ShouldBe(1);
        }

        // Second run on the unchanged vault is a no-op.
        using (var db = NewDb(postgres.ConnectionString))
        {
            var calibrator = new RelatedNotesCalibrator(
                db, Options.Create(o), SystemClock.Instance, NullLogger<RelatedNotesCalibrator>.Instance);
            (await calibrator.RunAsync(ct)).ShouldBe(CalibrationStatus.NotStale);
        }
    }

    [Fact]
    public async Task Persisted_auto_adds_query_doc_offset_to_doc_doc_youden()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await ClearCalibrationFieldsAsync();

        var cluster = FakeEmbeddingClient.DeterministicUnitVector("cluster");
        var n1 = await SeedNoteAsync(cluster);
        var n2 = await SeedNoteAsync(cluster);
        var n3 = await SeedNoteAsync(cluster);
        await SeedNoteAsync(FakeEmbeddingClient.DeterministicUnitVector("alpha"));
        await SeedNoteAsync(FakeEmbeddingClient.DeterministicUnitVector("beta"));
        await SeedEntityWithMentionsAsync(n1, n2, n3);

        var o = LowGateOptions();
        o.MaxDistanceFloor = 0.0;
        o.QueryDocOffset = 0.04;
        using (var db = NewDb(postgres.ConnectionString))
        {
            var calibrator = new RelatedNotesCalibrator(
                db, Options.Create(o), SystemClock.Instance, NullLogger<RelatedNotesCalibrator>.Instance);
            (await calibrator.RunAsync(ct)).ShouldBe(CalibrationStatus.Recalibrated);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var s = await probe.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId, ct);
        s.RelatedNotesMaxDistanceAuto!.Value.ShouldBe(0.04, 1e-9);
    }

    [Fact]
    public async Task Records_counts_but_leaves_auto_null_when_graph_too_sparse()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await ClearCalibrationFieldsAsync();

        // Enough notes to clear the note gate, but no shared-entity / shared-tag pairs → no positives.
        for (var i = 0; i < 5; i++)
        {
            await SeedNoteAsync(FakeEmbeddingClient.DeterministicUnitVector($"lonely-{i}"));
        }

        var o = LowGateOptions();
        using (var db = NewDb(postgres.ConnectionString))
        {
            var calibrator = new RelatedNotesCalibrator(
                db, Options.Create(o), SystemClock.Instance, NullLogger<RelatedNotesCalibrator>.Instance);
            (await calibrator.RunAsync(ct)).ShouldBe(CalibrationStatus.InsufficientData);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var s = await probe.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId, ct);
        s.RelatedNotesMaxDistanceAuto.ShouldBeNull();
        s.RelatedNotesAutoNoteCount.ShouldBe(5);
    }

    [Fact]
    public async Task Disabled_short_circuits()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await ClearCalibrationFieldsAsync();

        var o = LowGateOptions();
        o.AutoCalibrationEnabled = false;
        using var db = NewDb(postgres.ConnectionString);
        var calibrator = new RelatedNotesCalibrator(
            db, Options.Create(o), SystemClock.Instance, NullLogger<RelatedNotesCalibrator>.Instance);
        (await calibrator.RunAsync(ct)).ShouldBe(CalibrationStatus.Disabled);
    }

    private async Task ClearCalibrationFieldsAsync()
    {
        using var db = NewDb(postgres.ConnectionString);
        var s = await db.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId);
        s.RelatedNotesMaxDistanceAuto = null;
        s.RelatedNotesAutoNoteCount = null;
        s.RelatedNotesAutoEntityCount = null;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedNoteAsync(float[] vec)
    {
        var now = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(24);
        var id = Guid.CreateVersion7();
        using var db = NewDb(postgres.ConnectionString);
        db.Notes.Add(new Note
        {
            Id = id,
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "input",
            BodyOutput = "# Sample\n\nA paragraph.",
            RelativePath = $"Inbox/sample-{id:N}.md",
            Embedding = new Vector(vec),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedEntityWithMentionsAsync(params Guid[] noteIds)
    {
        var now = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(24);
        var entityId = Guid.CreateVersion7();
        using var db = NewDb(postgres.ConnectionString);
        db.Entities.Add(new Entity
        {
            Id = entityId,
            Kind = EntityKind.Concept,
            CanonicalName = "Shared Concept",
            Source = EntitySource.Llm,
            MentionCount = noteIds.Length,
            CreatedAt = now,
            UpdatedAt = now,
        });
        foreach (var noteId in noteIds)
        {
            db.Mentions.Add(new Mention
            {
                Id = Guid.CreateVersion7(),
                EntityId = entityId,
                NoteId = noteId,
                AnchorText = "shared",
                StartOffset = 0,
                EndOffset = 6,
                CreatedAt = now,
            });
        }
        await db.SaveChangesAsync();
    }

    private static CloudDbContext NewDb(string connStr)
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connStr, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
