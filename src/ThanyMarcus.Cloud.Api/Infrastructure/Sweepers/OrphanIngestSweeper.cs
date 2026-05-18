using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;

public sealed partial class OrphanIngestSweeper : BackgroundService
{
    public static readonly Duration OrphanThreshold = Duration.FromHours(1);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider services;
    private readonly IClock clock;
    private readonly ILogger<OrphanIngestSweeper> log;

    public OrphanIngestSweeper(IServiceProvider services, IClock clock, ILogger<OrphanIngestSweeper> log)
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

        var now = clock.GetCurrentInstant();
        var cutoff = now.Minus(OrphanThreshold);

        var orphans = await db.Notes
            .Where(n => n.Status == NoteStatus.Pending && n.CreatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var note in orphans)
        {
            var attachments = await db.Attachments
                .Where(a => a.NoteId == note.Id)
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

            note.Status     = NoteStatus.Failed;
            note.Provenance = System.Text.Json.JsonDocument.Parse(
                "{\"error\":\"orphan_no_finalize\"}");
            note.UpdatedAt  = now;
        }

        if (orphans.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            LogSweptOrphans(log, orphans.Count);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "OrphanIngestSweeper swept {Count} orphan notes")]
    private static partial void LogSweptOrphans(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "OrphanIngestSweeper failed to delete bucket key {StorageKey}")]
    private static partial void LogDeleteFailure(ILogger logger, Exception ex, string storageKey);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "OrphanIngestSweeper sweep iteration failed")]
    private static partial void LogSweepFailure(ILogger logger, Exception ex);
}
