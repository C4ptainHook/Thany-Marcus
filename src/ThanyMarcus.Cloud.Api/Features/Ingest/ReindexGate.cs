using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class ReindexGate(CloudDbContext db, IClock clock, RelatedNotesCalibrationSignal calibrationSignal)
{
    public async Task<bool> IsReindexingAsync(CancellationToken ct)
    {
        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        if (!settings.ReindexInProgress) return false;

        var pending = await db.Notes.CountAsync(
            n => n.DeletedAt == null && n.Status == NoteStatus.Ready && n.Embedding == null, ct);
        if (pending > 0) return true;

        settings.ReindexInProgress = false;
        settings.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        calibrationSignal.Trigger();
        return false;
    }
}
