using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public static class SyncEventsEndpoint
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static void MapSyncEventsEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/sync/events", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("GetSyncEvents");

    private static async Task HandleAsync(
        HttpContext http,
        IConfiguration config,
        IngestSseTranslator translator,
        CancellationToken ct)
    {
        var connectionString = config.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured");

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(ct);
        await WriteCommentAsync(http.Response, "connected", ct);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var listen = new NpgsqlCommand($"LISTEN {IngestEventKinds.Channel};", conn))
        {
            await listen.ExecuteNonQueryAsync(ct);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(http.Response, heartbeatCts.Token);

        conn.Notification += async (_, args) =>
        {
            var evt = translator.Translate(args.Payload);
            if (evt is null) return;
            try
            {
                await WriteSseAsync(http.Response, evt, translator, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* swallow client-side stream errors */ }
        };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { }
            catch { }
        }
    }

    private static async Task WriteSseAsync(
        HttpResponse response, IngestSseEvent evt, IngestSseTranslator translator, CancellationToken ct)
    {
        var json = translator.Serialize(evt);
        var payload = $"event: {evt.Kind}\ndata: {json}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken ct)
    {
        var payload = $": {comment}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        await response.Body.FlushAsync(ct);
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
