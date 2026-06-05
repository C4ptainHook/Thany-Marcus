using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public enum CalibrationStatus
{
    Disabled,
    NotStale,
    InsufficientData,
    WeakSeparation,
    Recalibrated,
}

public sealed partial class RelatedNotesCalibrator
{
    private readonly CloudDbContext db;
    private readonly IOptions<RelatedNotesOptions> opts;
    private readonly IClock clock;
    private readonly ILogger<RelatedNotesCalibrator> log;

    public RelatedNotesCalibrator(
        CloudDbContext db,
        IOptions<RelatedNotesOptions> opts,
        IClock clock,
        ILogger<RelatedNotesCalibrator> log)
    {
        this.db = db;
        this.opts = opts;
        this.clock = clock;
        this.log = log;
    }

    public async Task<CalibrationStatus> RunAsync(CancellationToken ct)
    {
        var o = opts.Value;
        if (!o.AutoCalibrationEnabled) return CalibrationStatus.Disabled;

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var (currentNotes, currentEntities) = await RelatedNotesCalibrationQueries.CountsAsync(db, ct);

        if (!RelatedNotesAutoCalibration.ShouldRecompute(
                settings.RelatedNotesMaxDistanceAuto,
                settings.RelatedNotesAutoNoteCount,
                settings.RelatedNotesAutoEntityCount,
                currentNotes, currentEntities, o))
        {
            return CalibrationStatus.NotStale;
        }

        var sample = await RelatedNotesCalibrationQueries.FetchSampleAsync(db, o.AutoRandomSampleSize, ct);
        if (sample.Positives.Count < o.AutoMinPositivePairs || sample.Negatives.Count < o.AutoMinNegativePairs)
        {
            // Note count cleared the gate but the graph is too sparse to separate. Record the counts so we
            // don't re-sample on every related query — we'll retry once the vault grows past the delta.
            await RecordCountsAsync(settings, currentNotes, currentEntities, ct);
            LogInsufficient(log, sample.Positives.Count, sample.Negatives.Count);
            return CalibrationStatus.InsufficientData;
        }

        var outcome = RelatedNotesAutoCalibration.RocAndYoudenThreshold(sample.Positives, sample.Negatives);
        if (outcome.Auc < o.AutoMinAuc)
        {
            await RecordCountsAsync(settings, currentNotes, currentEntities, ct);
            LogWeakSeparation(log, outcome.Auc, o.AutoMinAuc);
            return CalibrationStatus.WeakSeparation;
        }

        var queryDocScaled = outcome.Threshold + o.QueryDocOffset;
        var clamped = Math.Clamp(queryDocScaled, o.MaxDistanceFloor, o.MaxDistanceCeiling);
        var stored = RelatedNotesAutoCalibration.ApplyHysteresis(
            settings.RelatedNotesMaxDistanceAuto, clamped, o.AutoHysteresisMargin);

        settings.RelatedNotesMaxDistanceAuto = stored;
        settings.RelatedNotesAutoNoteCount = currentNotes;
        settings.RelatedNotesAutoEntityCount = currentEntities;
        settings.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        LogRecalibrated(log, stored, outcome.Auc, sample.Positives.Count, sample.Negatives.Count);
        return CalibrationStatus.Recalibrated;
    }

    private async Task RecordCountsAsync(CloudSettings settings, int notes, int entities, CancellationToken ct)
    {
        settings.RelatedNotesAutoNoteCount = notes;
        settings.RelatedNotesAutoEntityCount = entities;
        settings.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Related-notes Auto recalibrated: MaxDistance={Threshold} AUC={Auc} (pos={Positives}, neg={Negatives})")]
    private static partial void LogRecalibrated(ILogger logger, double threshold, double auc, int positives, int negatives);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Related-notes Auto skipped: insufficient pairs (pos={Positives}, neg={Negatives})")]
    private static partial void LogInsufficient(ILogger logger, int positives, int negatives);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Related-notes Auto skipped: weak separation AUC={Auc} < {MinAuc}")]
    private static partial void LogWeakSeparation(ILogger logger, double auc, double minAuc);
}
