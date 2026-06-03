using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class RealUrlFetcherClient : IUrlFetcherClient
{
    public const string HttpClientName = "UrlFetcher";

    private readonly IHttpClientFactory clientFactory;
    private readonly IUrlExtractor extractor;
    private readonly CloudDbContext db;
    private readonly IClock clock;
    private readonly IConfiguration config;
    private readonly ILogger<RealUrlFetcherClient> log;

    public RealUrlFetcherClient(
        IHttpClientFactory clientFactory,
        IUrlExtractor extractor,
        CloudDbContext db,
        IClock clock,
        IConfiguration config,
        ILogger<RealUrlFetcherClient> log)
    {
        this.clientFactory = clientFactory;
        this.extractor = extractor;
        this.db = db;
        this.clock = clock;
        this.config = config;
        this.log = log;
    }

    public async Task<UrlFetchOutcome> FetchAsync(Guid noteId, Attachment att, CancellationToken ct)
    {
        var url = att.Url ?? throw new UrlFetcherException("missing_url",
            $"Attachment {att.Id} has no url column populated");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UrlFetcherException("invalid_url", $"Invalid URL: {url}");
        }

        var contentType = await DetectContentTypeAsync(uri, ct);
        var primary = contentType.Split('/', 2)[0].ToLowerInvariant();
        var lower = contentType.ToLowerInvariant();

        if (primary == "text" || lower == "application/xhtml+xml" || string.IsNullOrWhiteSpace(contentType))
        {
            var result = await extractor.ExtractAsync(url, ct);
            var extra = UrlExtractionExtra.Build(result);
            var extracted = BuildExtractedTextFromResult(result);
            return new UrlFetchOutcome(
                ExtractedText: extracted,
                Extra: extra,
                RedirectedToAttachmentId: null,
                IsMinimal: result.MinimalReason is not null);
        }

        var target = BinaryRerouteMap.Resolve(contentType);

        if (target.TargetSidecar == ExtractionTaskSidecar.Video &&
            !config.GetValue("IngestSaga:Specialists:Video:Enabled", true))
        {
            throw new UrlFetcherException("video_reroute_disabled",
                "Video specialist worker is disabled");
        }

        var parentJobId = await db.ExtractionTasks
            .Where(t => t.AttachmentId == att.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (Guid?)t.IngestJobId)
            .FirstOrDefaultAsync(ct)
            ?? throw new UrlFetcherException("no_active_ingest_job",
                $"No extraction_task found for parent URL attachment {att.Id}");

        var childId = await InsertRerouteChildAsync(noteId, att, url, target, parentJobId, ct);
        await NotifySidecarAsync(target.TargetSidecar, ct);

        var rerouteExtra = BuildRerouteExtra(url, contentType, childId);
        return new UrlFetchOutcome(
            ExtractedText: null,
            Extra: rerouteExtra,
            RedirectedToAttachmentId: childId);
    }

    private async Task<string> DetectContentTypeAsync(Uri uri, CancellationToken ct)
    {
        var headTimeout = TimeSpan.FromSeconds(
            config.GetValue("IngestSaga:Sidecars:Url:HeadTimeoutSeconds", 5));
        var getTimeout = TimeSpan.FromSeconds(
            config.GetValue("IngestSaga:Sidecars:Url:GetTimeoutSeconds", 10));

        using var client = clientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UrlExtractor.UserAgent);

        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(headTimeout);
            using var headResp = await client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentType is { } ct1)
            {
                LogContentType(log, "head", uri.Host, ct1.MediaType ?? "");
                return ct1.MediaType ?? "";
            }
            if (headResp.StatusCode != HttpStatusCode.MethodNotAllowed && !headResp.IsSuccessStatusCode)
            {
                throw new UrlFetcherException("cannot_determine_content_type",
                    $"HEAD returned HTTP {(int)headResp.StatusCode}");
            }
        }
        catch (HttpRequestException) { /* fallthrough */ }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* fallthrough */ }

        try
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, uri);
            rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
            using var rangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rangeCts.CancelAfter(getTimeout);
            using var rangeResp = await client.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, rangeCts.Token);
            if (rangeResp.Content.Headers.ContentType is { } ct2)
            {
                LogContentType(log, "range-get", uri.Host, ct2.MediaType ?? "");
                return ct2.MediaType ?? "";
            }
        }
        catch (HttpRequestException) { /* fallthrough */ }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* fallthrough */ }

        throw new UrlFetcherException("cannot_determine_content_type",
            $"Could not determine Content-Type for {uri.Host}");
    }

    private async Task<Guid> InsertRerouteChildAsync(
        Guid noteId, Attachment parent, string url, BinaryRerouteTarget target,
        Guid ingestJobId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var childId = Guid.CreateVersion7();
        var clientAttachmentId = $"{parent.ClientAttachmentId}#redirected";
        var storageKey = $"external://{childId}";
        var emptyExtra = "{}";

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO attachments (
                id, note_id, client_attachment_id,
                kind, storage_provider, storage_bucket, storage_key,
                byte_size, mime_type, sha256, filename,
                status, extraction_status, extracted_text, extraction_error,
                extra, parent_attachment_id, extraction_cache_key, url,
                created_at, updated_at)
            VALUES (
                {childId}, {noteId}, {clientAttachmentId},
                {target.AttachmentKind}, 'external', '', {storageKey},
                NULL, {target.MimeType}, NULL, NULL,
                {AttachmentStatus.Uploaded}, {AttachmentExtractionStatus.Pending}, NULL, NULL,
                {emptyExtra}::jsonb, {parent.Id}, NULL, {url},
                {now}, {now});
            """, ct);

        var taskId = Guid.CreateVersion7();
        var queued = ExtractionTaskStatus.Queued;
        var eventsLog = "[]";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_tasks (
                id, ingest_job_id, attachment_id, target_sidecar,
                status, attempts, last_error,
                lease_owner, lease_expires_at, scheduled_at,
                started_at, finished_at, events_log,
                transition_version, created_at, updated_at)
            VALUES (
                {taskId}, {ingestJobId}, {childId}, {target.TargetSidecar},
                {queued}, 0, NULL,
                NULL, NULL, {now},
                NULL, NULL, {eventsLog}::jsonb,
                0, {now}, {now});
            """, ct);

        return childId;
    }

    private async Task NotifySidecarAsync(string sidecar, CancellationToken ct)
    {
        var channel = IngestNotifyChannels.ExtractionTasksNewForSidecar(sidecar);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd.Parameters.AddWithValue("chan", channel);
            cmd.Parameters.AddWithValue("payload", string.Empty);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }

    private static string BuildExtractedTextFromResult(UrlExtractionResult r)
    {
        if (r.MinimalReason is not null)
        {
            return "(URL captured, no preview available)";
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.ProviderName))
        {
            sb.Append('[').Append(r.ProviderName).Append("] ");
        }
        if (!string.IsNullOrWhiteSpace(r.Title))
        {
            sb.Append(r.Title);
        }
        else
        {
            sb.Append(r.CanonicalUrl);
        }
        if (!string.IsNullOrWhiteSpace(r.AuthorName))
        {
            sb.Append(" — ").Append(r.AuthorName);
        }
        if (!string.IsNullOrWhiteSpace(r.Description))
        {
            sb.Append('\n').Append(r.Description);
        }
        return sb.ToString();
    }

    private static JsonDocument BuildRerouteExtra(string url, string contentType, Guid childId)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("final_url", url);
            writer.WriteString("content_type", contentType);
            writer.WriteString("redirected_to", childId.ToString());
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "UrlFetcher detected content-type via {Method}: host={Host} ct={ContentType}")]
    private static partial void LogContentType(ILogger logger, string method, string host, string contentType);
}
