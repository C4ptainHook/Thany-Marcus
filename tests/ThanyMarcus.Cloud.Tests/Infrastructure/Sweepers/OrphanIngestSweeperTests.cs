using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sweepers;

[Collection(PostgresCollection.Name)]
public sealed class OrphanIngestSweeperTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sweeps_pending_note_older_than_threshold_and_deletes_bucket_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var now = Instant.FromUtc(2026, 5, 18, 12, 0);
        var clock = new FakeClock(now);
        var fakeStore = new FakeArtifactStore();
        var noteId = Guid.CreateVersion7();
        var storageKey = $"notes/{noteId}/{Guid.NewGuid()}.wav";
        fakeStore.Seed(storageKey, byteSize: 1024);

        var (services, _) = BuildScope(postgres, clock, fakeStore);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            db.Notes.Add(new Note
            {
                Id           = noteId,
                ClientNoteId = "cn-orphan",
                CapturedAt   = now.Minus(Duration.FromHours(2)),
                BodyInput    = "body",
                Status       = NoteStatus.Pending,
                CreatedAt    = now.Minus(Duration.FromHours(2)),
                UpdatedAt    = now.Minus(Duration.FromHours(2)),
            });
            db.Attachments.Add(new Attachment
            {
                Id                 = Guid.CreateVersion7(),
                NoteId             = noteId,
                ClientAttachmentId = "a1",
                Kind               = AttachmentKind.Voice,
                StorageProvider    = "s3",
                StorageBucket      = "test",
                StorageKey         = storageKey,
                Status             = AttachmentStatus.AwaitingUpload,
                Extra              = JsonDocument.Parse("{}"),
                CreatedAt          = now.Minus(Duration.FromHours(2)),
                UpdatedAt          = now.Minus(Duration.FromHours(2)),
            });
            await db.SaveChangesAsync(ct);
        }

        var sweeper = new OrphanIngestSweeper(services, clock, NullLogger<OrphanIngestSweeper>.Instance);
        await sweeper.SweepOnceAsync(ct);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            var note = await db.Notes.SingleAsync(n => n.Id == noteId, ct);
            note.Status.ShouldBe(NoteStatus.Failed);
            note.Provenance!.RootElement.GetProperty("error").GetString().ShouldBe("orphan_no_finalize");
        }

        fakeStore.Objects.ContainsKey(storageKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Leaves_recent_pending_notes_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var now = Instant.FromUtc(2026, 5, 18, 12, 0);
        var clock = new FakeClock(now);
        var (services, _) = BuildScope(postgres, clock, new FakeArtifactStore());

        var noteId = Guid.CreateVersion7();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            db.Notes.Add(new Note
            {
                Id           = noteId,
                ClientNoteId = "cn-fresh",
                CapturedAt   = now.Minus(Duration.FromMinutes(10)),
                BodyInput    = "body",
                Status       = NoteStatus.Pending,
                CreatedAt    = now.Minus(Duration.FromMinutes(10)),
                UpdatedAt    = now.Minus(Duration.FromMinutes(10)),
            });
            await db.SaveChangesAsync(ct);
        }

        var sweeper = new OrphanIngestSweeper(services, clock, NullLogger<OrphanIngestSweeper>.Instance);
        await sweeper.SweepOnceAsync(ct);

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            var note = await db.Notes.SingleAsync(n => n.Id == noteId, ct);
            note.Status.ShouldBe(NoteStatus.Pending);
        }
    }

    private static (IServiceProvider services, IClock clock) BuildScope(
        PostgresFixture postgres, IClock clock, FakeArtifactStore store)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(clock);
        sc.AddSingleton<TimestampInterceptor>();
        sc.AddDbContext<CloudDbContext>((sp, opts) => opts
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));
        sc.AddSingleton<Cloud.Api.Infrastructure.Storage.IArtifactStore>(store);
        return (sc.BuildServiceProvider(), clock);
    }
}
