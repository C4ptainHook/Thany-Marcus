using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class RollingBackDnsHandler(
    PortalDbContext db,
    IClock clock,
    ICloudflareDnsClient cloudflare,
    ILogger<RollingBackDnsHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.RollingBackDns;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);

        var recordId = ReadDnsRecordId(job);
        if (recordId is null)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "no_dns_record_found_in_events_log",
            });
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
            return;
        }

        try
        {
            await cloudflare.DeleteAsync(recordId, string.Empty, ct);
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "dns_deleted",
                ["record_id"] = recordId,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            LogDeleteFailure(log, ex, job.Id, recordId);
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "dns_delete_failed",
                ["record_id"] = recordId,
                ["error"] = ex.Message,
            });
        }

        await SagaTransitions.TransitionAsync(
            db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
    }

    private static string? ReadDnsRecordId(ProvisioningJob job)
    {
        var root = job.EventsLog.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return null;
        string? latest = null;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("record_id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String)
            {
                latest = idEl.GetString();
            }
        }
        return latest;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "RollingBackDnsHandler: Cloudflare delete failed for job {JobId} record {RecordId}; proceeding to tf rollback")]
    private static partial void LogDeleteFailure(ILogger logger, Exception ex, Guid jobId, string recordId);
}
