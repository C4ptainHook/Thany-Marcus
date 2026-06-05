using Microsoft.EntityFrameworkCore;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class NoteVectorQueriesTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returns_results_in_distance_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var target = UnitVector(seed: 1);
        using (var db = NewDb(postgres.ConnectionString))
        {
            db.Notes.Add(SeedNote(target, hoursAgo: 24));
            db.Notes.Add(SeedNote(UnitVector(seed: 2), hoursAgo: 24));
            db.Notes.Add(SeedNote(UnitVector(seed: 3), hoursAgo: 24));
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 3, excludeNoteId: null, ct: ct);

        results.Count.ShouldBe(3);
        results[0].Distance.ShouldBeLessThan(results[1].Distance);
        results[1].Distance.ShouldBeLessThanOrEqualTo(results[2].Distance);
    }

    [Fact]
    public async Task Excludes_deleted_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var target = UnitVector(seed: 10);

        Guid deletedId;
        using (var db = NewDb(postgres.ConnectionString))
        {
            var deleted = SeedNote(target, hoursAgo: 24);
            deleted.DeletedAt = SystemClock.Instance.GetCurrentInstant();
            deletedId = deleted.Id;
            db.Notes.Add(deleted);
            db.Notes.Add(SeedNote(UnitVector(seed: 11), hoursAgo: 24));
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 5, excludeNoteId: null, ct: ct);

        results.ShouldNotContain(r => r.Id == deletedId);
    }

    [Fact]
    public async Task Does_not_exclude_recent_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var target = UnitVector(seed: 20);

        Guid recentId;
        Guid olderId;
        using (var db = NewDb(postgres.ConnectionString))
        {
            var recent = SeedNote(target, hoursAgo: 0);
            recentId = recent.Id;
            var older = SeedNote(target, hoursAgo: 24);
            olderId = older.Id;
            db.Notes.Add(recent);
            db.Notes.Add(older);
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 5, excludeNoteId: null, ct: ct);

        results.ShouldContain(r => r.Id == recentId);
        results.ShouldContain(r => r.Id == olderId);
    }

    [Fact]
    public async Task Excludes_self_when_excludeNoteId_provided()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var target = UnitVector(seed: 30);

        Guid selfId;
        using (var db = NewDb(postgres.ConnectionString))
        {
            var self = SeedNote(target, hoursAgo: 24);
            selfId = self.Id;
            db.Notes.Add(self);
            db.Notes.Add(SeedNote(UnitVector(seed: 31), hoursAgo: 24));
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 5, excludeNoteId: selfId, ct: ct);

        results.ShouldNotContain(r => r.Id == selfId);
    }

    [Fact]
    public async Task Excludes_notes_with_null_embedding()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var target = UnitVector(seed: 40);

        using (var db = NewDb(postgres.ConnectionString))
        {
            var note = SeedNote(target, hoursAgo: 24);
            note.Embedding = null;
            db.Notes.Add(note);
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 5, excludeNoteId: null, ct: ct);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Returns_up_to_fanout_candidates_with_embeddings()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var target = UnitVector(seed: 50);

        using (var db = NewDb(postgres.ConnectionString))
        {
            for (var i = 0; i < 8; i++)
            {
                db.Notes.Add(SeedNote(target, hoursAgo: 24));
            }
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb(postgres.ConnectionString);
        var results = await NoteVectorQueries.NearestAsync(
            probe, target, fanout: 5, excludeNoteId: null, ct: ct);

        results.Count.ShouldBe(5);
        results.ShouldAllBe(r => r.Embedding.Length == 256);
    }

    private static Note SeedNote(float[] vec, int hoursAgo)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var ts = now - Duration.FromHours(hoursAgo);
        return new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = ts,
            Status = NoteStatus.Ready,
            BodyInput = "input",
            BodyOutput = "# Title\n\nA body paragraph.",
            RelativePath = $"Inbox/note-{Guid.NewGuid():N}.md",
            Embedding = new Vector(vec),
            CreatedAt = ts,
            UpdatedAt = ts,
        };
    }

    private static float[] UnitVector(int seed)
    {
        var rng = new Random(seed);
        var v = new float[256];
        double sum = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var x = (float)(rng.NextDouble() * 2.0 - 1.0);
            v[i] = x;
            sum += x * x;
        }
        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            var inv = (float)(1.0 / norm);
            for (var i = 0; i < v.Length; i++) v[i] *= inv;
        }
        return v;
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
