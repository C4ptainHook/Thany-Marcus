using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

// MaterializeAsync/EnqueueRegenAsync add rows to db but do NOT call SaveChanges — the caller
// must run SaveChanges within its outer transaction so the hub note + ingest_jobs land atomically
// with the mentions that triggered them.
public static class HubMaterializer
{
    public static Note MaterializeAsync(CloudDbContext db, Entity entity, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetCurrentInstant();
        var hubNote = new Note
        {
            Id = Guid.CreateVersion7(),
            CapturedAt = now,
            BodyInput = "",
            IsHub = true,
            HubEntityId = entity.Id,
            RelativePath = $"_Entities/{entity.Kind}/{entity.CanonicalName}.md",
            Status = NoteStatus.Pending,
        };
        db.Notes.Add(hubNote);
        entity.HubNoteId = hubNote.Id;
        db.IngestJobs.Add(new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = hubNote.Id,
            Kind = IngestJobKind.HubRegen,
            Status = IngestJobStatus.Composing,
            ScheduledAt = now,
        });
        return hubNote;
    }

    public static void EnqueueRegen(CloudDbContext db, Entity entity, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(clock);

        if (entity.HubNoteId is null) return;
        db.IngestJobs.Add(new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = entity.HubNoteId.Value,
            Kind = IngestJobKind.HubRegen,
            Status = IngestJobStatus.Composing,
            ScheduledAt = clock.GetCurrentInstant(),
        });
    }
}
