using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Tests.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Entities;

[Collection(PostgresCollection.Name)]
public sealed class HubMaterializerTests(PostgresFixture postgres)
{
    private static readonly string[] JohnnyAliases = { "Johnny" };

    [Fact]
    public async Task Materialize_inserts_hub_note_and_hub_regen_ingest_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var clock = SystemClock.Instance;
        var now = clock.GetCurrentInstant();
        using (var seed = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            seed.Entities.Add(new Entity
            {
                Id            = Guid.CreateVersion7(),
                Kind          = EntityKind.Person,
                CanonicalName = "John Smith",
                Aliases       = JohnnyAliases,
                Source        = EntitySource.Llm,
                IsProvisional = true,
                MentionCount  = 3,
                CreatedAt     = now,
                UpdatedAt     = now,
            });
            await seed.SaveChangesAsync(ct);
        }

        Guid entityId;
        Note hubNote;
        using (var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            var entity = await db.Entities.SingleAsync(ct);
            entityId = entity.Id;
            hubNote = HubMaterializer.MaterializeAsync(db, entity, clock);
            await db.SaveChangesAsync(ct);
        }

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var afterEntity = await probe.Entities.SingleAsync(e => e.Id == entityId, ct);
        afterEntity.HubNoteId.ShouldBe(hubNote.Id);

        var savedNote = await probe.Notes.SingleAsync(n => n.Id == hubNote.Id, ct);
        savedNote.IsHub.ShouldBeTrue();
        savedNote.HubEntityId.ShouldBe(entityId);
        savedNote.RelativePath.ShouldBe("_Entities/person/John Smith.md");
        savedNote.Status.ShouldBe(NoteStatus.Pending);

        var hubJob = await probe.IngestJobs.SingleAsync(j => j.NoteId == hubNote.Id, ct);
        hubJob.Kind.ShouldBe(IngestJobKind.HubRegen);
        hubJob.Status.ShouldBe(IngestJobStatus.Composing);
    }

    [Fact]
    public async Task EnqueueRegen_inserts_second_hub_regen_job_for_existing_hub()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var clock = SystemClock.Instance;
        var now = clock.GetCurrentInstant();

        Guid hubNoteId;
        Guid entityId;
        using (var seed = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            var hubNote = new Note
            {
                Id           = Guid.CreateVersion7(),
                CapturedAt   = now,
                Status       = NoteStatus.Ready,
                BodyInput    = "",
                IsHub        = true,
                RelativePath = "_Entities/person/X.md",
                CreatedAt    = now,
                UpdatedAt    = now,
            };
            hubNoteId = hubNote.Id;
            var entity = new Entity
            {
                Id            = Guid.CreateVersion7(),
                Kind          = EntityKind.Person,
                CanonicalName = "X",
                Source        = EntitySource.Llm,
                IsProvisional = true,
                HubNoteId     = hubNote.Id,
                MentionCount  = 5,
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            entityId = entity.Id;
            hubNote.HubEntityId = entityId;
            seed.Notes.Add(hubNote);
            seed.Entities.Add(entity);
            // Pre-existing terminal hub-regen job to avoid active-per-note conflict.
            seed.IngestJobs.Add(new IngestJob
            {
                Id           = Guid.CreateVersion7(),
                NoteId       = hubNoteId,
                Kind         = IngestJobKind.HubRegen,
                Status       = IngestJobStatus.Succeeded,
                ScheduledAt  = now,
                FinishedAt   = now,
                CreatedAt    = now,
                UpdatedAt    = now,
            });
            await seed.SaveChangesAsync(ct);
        }

        using (var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            HubMaterializer.EnqueueRegen(db, entity, clock);
            await db.SaveChangesAsync(ct);
        }

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var jobs = await probe.IngestJobs.Where(j => j.NoteId == hubNoteId).ToListAsync(ct);
        jobs.Count.ShouldBe(2);
        jobs.ShouldContain(j => j.Status == IngestJobStatus.Composing && j.Kind == IngestJobKind.HubRegen);
    }
}
