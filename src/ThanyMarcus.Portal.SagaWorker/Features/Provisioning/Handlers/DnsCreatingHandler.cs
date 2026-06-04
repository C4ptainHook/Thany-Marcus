using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class DnsCreatingHandler(
    PortalDbContext db,
    IClock clock,
    ICloudflareDnsClient cloudflare,
    ILogger<DnsCreatingHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.DnsCreating;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        if (cloud.DnsStartedAt is null)
        {
            cloud.DnsStartedAt = clock.GetCurrentInstant();
        }

        var ip = ReadIpFromOutputs(job);
        if (ip is null)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["error"] = "missing_ip_in_tf_outputs",
                ["rollback_reason"] = "dns_failed",
            });
            job.LastError = "no IP in tf_outputs";
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
            return;
        }

        DnsRecord record;
        try
        {
            record = await cloudflare.CreateAAsync(SubdomainOf(cloud.Hostname), ip, string.Empty, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            LogDnsFailure(log, ex, job.Id);
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["error"] = $"dns_create_failed: {ex.Message}",
                ["rollback_reason"] = "dns_failed",
            });
            job.LastError = "Cloudflare DNS create failed";
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
            return;
        }

        cloud.Subdomain = SubdomainOf(cloud.Hostname);

        EventsLogAppender.Append(job, clock, Phase, new JsonObject
        {
            ["event"] = "dns_created",
            ["record_id"] = record.Id,
            ["subdomain"] = record.Subdomain,
            ["ip"] = record.Ip.ToString(),
        });

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        await SagaTransitions.TransitionAsync(
            db, clock, job, SagaStatus.AwaitingCloudCallback, Duration.Zero, ct: ct);
    }

    private static IPAddress? ReadIpFromOutputs(ProvisioningJob job)
    {
        if (job.TfOutputs is null) return null;
        var root = job.TfOutputs.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("ip", out var ipEntry)) return null;
        if (ipEntry.ValueKind != JsonValueKind.Object) return null;
        if (!ipEntry.TryGetProperty("value", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString();
        return raw is not null && IPAddress.TryParse(raw, out var ip) ? ip : null;
    }

    private static string SubdomainOf(string hostname)
    {
        var idx = hostname.IndexOf('.', StringComparison.Ordinal);
        return idx < 0 ? hostname : hostname[..idx];
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DnsCreatingHandler: Cloudflare CreateA failed for job {JobId}")]
    private static partial void LogDnsFailure(ILogger logger, Exception ex, Guid jobId);
}
