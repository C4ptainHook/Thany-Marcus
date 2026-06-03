using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class AwaitingCertHandler(
    PortalDbContext db,
    IClock clock,
    IHttpClientFactory httpFactory,
    ICloudAdminTokenAccessor adminTokens,
    ILogger<AwaitingCertHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.AwaitingCert;

    public const string HttpClientName = "cert-poll";

    private static readonly Duration CertDeadline   = Duration.FromMinutes(30);
    private static readonly Duration FastCadence    = Duration.FromSeconds(5);
    private static readonly Duration SlowCadence    = Duration.FromSeconds(30);
    private static readonly Duration FastWindow     = Duration.FromSeconds(60);

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        if (cloud.CertStartedAt is null)
        {
            cloud.CertStartedAt = clock.GetCurrentInstant();
        }

        var now = clock.GetCurrentInstant();
        var phaseStarted = job.PhaseStartedAt ?? now;
        var age = now - phaseStarted;

        if (age >= CertDeadline)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "cert_deadline_exceeded",
                ["age_seconds"] = (long)age.TotalSeconds,
            });
            job.LastError = "cert provisioning deadline exceeded";
            await SagaTransitions.TransitionToTerminalAsync(
                db, clock, job, cloud, SagaStatus.FailedCert, ct);
            return;
        }

        bool certReady;
        try
        {
            certReady = await PollHealthAsync(cloud, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogPollFailure(log, ex, job.Id);
            certReady = false;
        }

        if (certReady)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject { ["event"] = "cert_ready" });
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.IssuingPluginToken, Duration.Zero, ct: ct);
            return;
        }

        var cadence = age < FastWindow ? FastCadence : SlowCadence;
        await SagaTransitions.RescheduleAsync(db, clock, job, cadence, ct);
    }

    private async Task<bool> PollHealthAsync(Cloud cloud, CancellationToken ct)
    {
        var adminToken = await adminTokens.GetPlaintextAsync(cloud.Id, ct);
        using var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{cloud.Hostname}/admin/health");
        if (!string.IsNullOrEmpty(adminToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        }
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<CloudAdminHealthResponse>(ct);
        return payload?.CertReady ?? false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "AwaitingCertHandler poll failed for job {JobId} (will retry)")]
    private static partial void LogPollFailure(ILogger logger, Exception ex, Guid jobId);
}
