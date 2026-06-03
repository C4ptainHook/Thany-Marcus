using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Events;

public static class CloudEventsEndpoints
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapCloudEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/clouds/{id:guid}/events", async (
            Guid id,
            HttpContext http,
            ClaimsPrincipal user,
            PortalDbContext db,
            NpgsqlConnectionFactory connFactory,
            SagaEventTranslator translator,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var cloud = await db.Clouds.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);
            if (cloud is null) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (cloud.UserId != userId) { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await http.Response.Body.FlushAsync(ct);

            await using var conn = await connFactory.OpenAsync(ct);
            await using (var listen = new NpgsqlCommand("LISTEN provisioning_job_changed;", conn))
                await listen.ExecuteNonQueryAsync(ct);
            await using (var listen2 = new NpgsqlCommand(
                $"LISTEN {PostgresProvisioningEventBus.PluginTokenIssuedChannel};", conn))
                await listen2.ExecuteNonQueryAsync(ct);

            var state = await LoadJobStateAsync(db, id, cloud, ct);
            foreach (var evt in translator.InitialEvents(state))
                await WriteSseAsync(http.Response, evt, ct);

            if (state is not null && SagaStatus.IsTerminal(state.Status))
            {
                await WriteCommentAsync(http.Response, "closed", ct);
                return;
            }

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = RunHeartbeatAsync(http.Response, heartbeatCts.Token);

            conn.Notification += async (_, args) =>
            {
                if (args.Channel == PostgresProvisioningEventBus.PluginTokenIssuedChannel)
                {
                    var evt = TryParsePluginTokenIssued(args.Payload, id);
                    if (evt is not null)
                    {
                        await WriteSseAsync(http.Response, evt, ct);
                    }
                    return;
                }

                if (!Guid.TryParse(args.Payload, out var changedJobId)) return;
                if (state is null || state.JobId != changedJobId) return;
                var current = await LoadJobStateAsync(db, id, cloud, ct);
                if (current is null) return;
                foreach (var evt in translator.Translate(state, current))
                    await WriteSseAsync(http.Response, evt, ct);
                state = current;
                if (SagaStatus.IsTerminal(state.Status))
                {
                    await WriteCommentAsync(http.Response, "closed", ct);
                    heartbeatCts.Cancel();
                }
            };

            try
            {
                while (!ct.IsCancellationRequested && !heartbeatCts.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat; } catch { }
            }
        })
        .RequireAuthorization();
    }

    private static async Task<JobStateSnapshot?> LoadJobStateAsync(
        PortalDbContext db, Guid cloudId, Cloud cloud, CancellationToken ct)
    {
        var job = await db.ProvisioningJobs
            .Where(j => j.CloudId == cloudId && j.Kind != SagaKinds.Cancel)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new { j.Id, j.Status, j.LastError })
            .FirstOrDefaultAsync(ct);
        if (job is null) return null;
        var cancelRequested = await db.Clouds.IgnoreQueryFilters()
            .Where(c => c.Id == cloudId)
            .Select(c => c.CancelRequestedAt != null)
            .FirstAsync(ct);
        return new JobStateSnapshot(job.Id, cloudId, job.Status, cloud.Hostname, cloud.VmIp, job.LastError, cancelRequested);
    }

    private static async Task WriteSseAsync(HttpResponse response, WizardSseEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<object>(evt, JsonOpts);
        var payload = $"event: {evt.Type}\ndata: {json}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken ct)
    {
        var payload = $": {comment}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        await response.Body.FlushAsync(ct);
    }

    private static WizardSseEvent.PluginTokenIssuedEvent? TryParsePluginTokenIssued(string payload, Guid expectedCloudId)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cloudId", out var idProp)
                || !Guid.TryParse(idProp.GetString(), out var cloudId)
                || cloudId != expectedCloudId)
            {
                return null;
            }
            var rawToken = root.GetProperty("rawToken").GetString() ?? "";
            var deepLink = root.GetProperty("deepLink").GetString() ?? "";
            return WizardSseEvent.PluginTokenIssued(rawToken, deepLink);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task RunHeartbeatAsync(HttpResponse response, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await WriteCommentAsync(response, "heartbeat", ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
