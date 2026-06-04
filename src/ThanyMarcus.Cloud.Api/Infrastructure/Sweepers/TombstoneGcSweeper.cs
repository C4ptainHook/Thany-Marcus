using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;

// Past the 14-day cheap-revive window a tombstone becomes a real delete: free the attachment
// blobs, then hard-delete the row (FK cascade clears attachments / ingest_jobs / mentions). The
// retention window is >= the local trash retention so an un-trash always lands on a live tombstone.
public sealed partial class TombstoneGcSweeper : BackgroundService
{
    public static readonly Duration RetentionWindow = Duration.FromDays(14);
    public static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    public const int BatchSize = 200;

    private readonly IServiceProvider services;
    private readonly IClock clock;
    private readonly ILogger<TombstoneGcSweeper> log;

    public TombstoneGcSweeper(IServiceProvider services, IClock clock, ILogger<TombstoneGcSweeper> log)
    {
        this.services = services;
        this.clock = clock;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogSweepFailure(log, ex);
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        var cutoff = clock.GetCurrentInstant().Minus(RetentionWindow);

        var expired = await db.Notes
            .Where(n => n.DeletedAt != null && n.DeletedAt < cutoff)
            .OrderBy(n => n.DeletedAt)
            .Take(BatchSize)
            .Select(n => n.Id)
            .ToListAsync(ct);
        if (expired.Count == 0) return;

        var attachments = await db.Attachments
            .Where(a => expired.Contains(a.NoteId))
            .Select(a => new { a.Kind, a.StorageKey })
            .ToListAsync(ct);

        foreach (var att in attachments)
        {
            if (!AttachmentKind.IsBinary(att.Kind)) continue;
            try
            {
                await store.DeleteAsync(att.StorageKey, ct);
            }
            catch (Exception ex)
            {
                LogDeleteFailure(log, ex, att.StorageKey);
            }
        }

        var purged = await db.Notes
            .Where(n => expired.Contains(n.Id))
            .ExecuteDeleteAsync(ct);

        LogPurged(log, purged);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "TombstoneGcSweeper hard-deleted {Count} expired tombstones")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "TombstoneGcSweeper failed to delete bucket key {StorageKey}")]
    private static partial void LogDeleteFailure(ILogger logger, Exception ex, string storageKey);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "TombstoneGcSweeper sweep iteration failed")]
    private static partial void LogSweepFailure(ILogger logger, Exception ex);
}
