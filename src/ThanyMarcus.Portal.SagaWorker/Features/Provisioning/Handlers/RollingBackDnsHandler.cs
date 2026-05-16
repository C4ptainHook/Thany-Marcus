using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class RollingBackDnsHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ICloudflareDnsClient cloudflare,
    ILogger<RollingBackDnsHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.RollingBackDns;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

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

        var dek = new byte[32];
        byte[]? cloudflareToken = null;
        try
        {
            string tokenString;
            if (await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                cloudflareToken = await providerVault.DecryptAsync(cloud.UserId, KnownProviders.Cloudflare, dek, ct);
                tokenString = cloudflareToken is not null
                    ? Encoding.UTF8.GetString(cloudflareToken)
                    : string.Empty;
            }
            else
            {
                tokenString = string.Empty;
            }

            try
            {
                await cloudflare.DeleteAsync(recordId, tokenString, ct);
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
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (cloudflareToken is not null) CryptographicOperations.ZeroMemory(cloudflareToken);
        }
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
