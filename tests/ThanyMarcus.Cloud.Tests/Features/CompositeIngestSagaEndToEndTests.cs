using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Slow")]
public sealed class CompositeIngestSagaEndToEndTests(PostgresFixture postgres)
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Composite_saga_full_lifecycle_with_reprocess_and_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        var fakeStore = new FakeArtifactStore();
        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            DisableHostedServices = false,
            OrchestratorIdlePollMs = 500,
            ExtractionTasksIdlePollMs = 500,
            CustomizeServices = services =>
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    var t = services[i].ServiceType;
                    if (t == typeof(IArtifactStore)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IUrlFetcherClient)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IVlmClient)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IDoclingClient)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IParakeetClient)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IVideoSplitterClient)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight.IDocumentPreflighter)
                     || t == typeof(ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight.IAudioPreflighter))
                    {
                        services.RemoveAt(i);
                    }
                }
                services.AddSingleton<IArtifactStore>(fakeStore);
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IUrlFetcherClient,
                    ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs.StubUrlFetcherClient>();
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IVlmClient,
                    ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs.StubVlmClient>();
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IDoclingClient>(
                    new InMemoryDoclingClient("[stub docling extraction]"));
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IParakeetClient>(
                    new InMemoryParakeetClient("[stub parakeet transcription]"));
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.IVideoSplitterClient>(
                    new InMemoryVideoSplitterClient());
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight.IDocumentPreflighter,
                    AlwaysPassDocumentPreflighter>();
                services.AddSingleton<ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight.IAudioPreflighter,
                    AlwaysPassAudioPreflighter>();
            },
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var startedSince = DateTimeOffset.UtcNow.AddSeconds(-1);
        var clientNoteId = Guid.NewGuid().ToString();
        var initReq = new IngestInitRequest(
            ClientNoteId: clientNoteId,
            CapturedAt:   DateTimeOffset.UtcNow,
            Body:         "composite saga e2e test body",
            Attachments:
            [
                new("a-url",   AttachmentKind.Url,   null,         null, null,  null,
                    JsonDocument.Parse("{\"url\":\"https://example.test/article\"}").RootElement),
                new("a-image", AttachmentKind.Image, "image/png",  1024L, "sha-img",   "img.png",
                    JsonDocument.Parse("{}").RootElement),
                new("a-voice", AttachmentKind.Voice, "audio/wav",  2048L, "sha-voice", "memo.wav",
                    JsonDocument.Parse("{}").RootElement),
                new("a-pdf",   AttachmentKind.File,  "application/pdf", 4096L, "sha-pdf", "doc.pdf",
                    JsonDocument.Parse("{}").RootElement),
            ]);

        var initResp = await (await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), initReq, ct))
            .Content.ReadFromJsonAsync<IngestInitResponse>(ct);
        initResp.ShouldNotBeNull();
        initResp!.Uploads.Count.ShouldBe(3);

        foreach (var u in initResp.Uploads)
        {
            var key = KeyFromUrl(u.UploadUrl);
            var size = u.ClientAttachmentId switch
            {
                "a-image" => 1024L,
                "a-voice" => 2048L,
                "a-pdf"   => 4096L,
                _         => 0L,
            };
            fakeStore.Seed(key, byteSize: size);
        }

        var finalizeReq = new IngestFinalizeRequest(
            initResp.Uploads
                .Select(u => new IngestFinalizeUploadedAttachment(
                    u.AttachmentId,
                    u.ClientAttachmentId switch
                    {
                        "a-image" => "sha-img",
                        "a-voice" => "sha-voice",
                        "a-pdf"   => "sha-pdf",
                        _         => "sha",
                    },
                    u.ClientAttachmentId switch
                    {
                        "a-image" => 1024L,
                        "a-voice" => 2048L,
                        "a-pdf"   => 4096L,
                        _         => 0L,
                    }))
                .ToList());

        var finalizeResp = await client.PostAsJsonAsync(
            new Uri($"/api/ingest/{initResp.NoteId}/finalize", UriKind.Relative), finalizeReq, ct);
        finalizeResp.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForTerminalAsync(postgres.ConnectionString, initResp.NoteId, ct);

        var eventsLog = await ReadEventsLogAsync(postgres.ConnectionString, initResp.NoteId, ct);
        AssertPhaseSequence(eventsLog);

        var pullResp = await client.GetAsync(
            new Uri($"/api/sync/pull?since={Uri.EscapeDataString(startedSince.ToString("O"))}&include=provenance",
                UriKind.Relative), ct);
        pullResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pullBody = await pullResp.Content.ReadFromJsonAsync<SyncPullResponse>(ct);
        pullBody.ShouldNotBeNull();
        var item = pullBody!.Items.SingleOrDefault(i => i.NoteId == initResp.NoteId);
        item.ShouldNotBeNull();
        item!.RelativePath.ShouldBe($"Inbox/{initResp.NoteId}.md");
        item.Body.ShouldContain("[stub URL extraction]");
        item.Body.ShouldContain("[stub VLM description");
        item.Body.ShouldContain("[stub parakeet transcription]");
        item.Body.ShouldContain("[stub docling extraction]");
        item.Attachments.Count.ShouldBe(4);
        item.Attachments
            .Where(a => a.Kind != AttachmentKind.Url)
            .ShouldAllBe(a => a.DownloadUrl != null);
        item.Provenance.ShouldNotBeNull();
        var prov = item.Provenance!.Value;
        prov.GetProperty("extraction_summary").GetArrayLength().ShouldBeGreaterThan(0);
        var totalExtracted = 0;
        var totalTotal = 0;
        foreach (var entry in prov.GetProperty("extraction_summary").EnumerateArray())
        {
            totalTotal += entry.GetProperty("total").GetInt32();
            totalExtracted += entry.GetProperty("extracted").GetInt32();
        }
        totalTotal.ShouldBe(4);
        totalExtracted.ShouldBe(4);

        var reprocessResp = await client.PostAsync(
            new Uri($"/api/notes/{initResp.NoteId}/reprocess", UriKind.Relative), null, ct);
        reprocessResp.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForReadyAfterReprocessAsync(postgres.ConnectionString, initResp.NoteId, ct);

        var pullResp2 = await client.GetAsync(
            new Uri($"/api/sync/pull?include=provenance", UriKind.Relative), ct);
        var pullBody2 = await pullResp2.Content.ReadFromJsonAsync<SyncPullResponse>(ct);
        pullBody2.ShouldNotBeNull();
        var item2 = pullBody2!.Items.SingleOrDefault(i => i.NoteId == initResp.NoteId);
        item2.ShouldNotBeNull();
        item2!.Provenance.ShouldNotBeNull();
        item2.Provenance!.Value.TryGetProperty("cache_hits", out _).ShouldBeTrue();
        item2.Provenance!.Value.TryGetProperty("extraction_summary", out _).ShouldBeTrue();

        var deleteResp = await client.DeleteAsync(
            new Uri($"/api/notes/{initResp.NoteId}", UriKind.Relative), ct);
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var reprocessAfterDelete = await client.PostAsync(
            new Uri($"/api/notes/{initResp.NoteId}/reprocess", UriKind.Relative), null, ct);
        reprocessAfterDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await WaitForDeletedAsync(postgres.ConnectionString, initResp.NoteId, ct);

        var pullResp3 = await client.GetAsync(
            new Uri($"/api/sync/pull?include=provenance", UriKind.Relative), ct);
        var pullBody3 = await pullResp3.Content.ReadFromJsonAsync<SyncPullResponse>(ct);
        pullBody3.ShouldNotBeNull();
        var item3 = pullBody3!.Items.SingleOrDefault(i => i.NoteId == initResp.NoteId);
        item3.ShouldNotBeNull();
        item3!.DeletedAt.ShouldNotBeNull();
    }

    private static void AssertPhaseSequence(IReadOnlyList<JsonElement> events)
    {
        string[] expectedTransitions =
        [
            IngestJobStatus.Composing,
            IngestJobStatus.Routing,
            IngestJobStatus.ExtractingEntities,
            IngestJobStatus.Embedding,
            IngestJobStatus.Succeeded,
        ];

        var observed = events
            .Where(e => e.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("to").GetString()!)
            .ToList();

        var idx = 0;
        foreach (var phase in expectedTransitions)
        {
            var found = observed.IndexOf(phase, idx);
            found.ShouldBeGreaterThanOrEqualTo(0,
                $"transition to '{phase}' missing or out of order in events_log (observed={string.Join(",", observed)})");
            idx = found + 1;
        }
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadEventsLogAsync(
        string connStr, Guid noteId, CancellationToken ct)
    {
        using var db = NewDb(connStr);
        var job = await db.IngestJobs.AsNoTracking()
            .Where(j => j.NoteId == noteId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstAsync(ct);
        var list = new List<JsonElement>();
        if (job.EventsLog.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in job.EventsLog.RootElement.EnumerateArray())
            {
                list.Add(el.Clone());
            }
        }
        return list;
    }

    private static async Task WaitForTerminalAsync(
        string connStr, Guid noteId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TerminalTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var db = NewDb(connStr);
            var note = await db.Notes.AsNoTracking()
                .SingleOrDefaultAsync(n => n.Id == noteId, ct);
            if (note is not null &&
                (note.Status == NoteStatus.Ready || note.Status == NoteStatus.Failed))
            {
                return;
            }
            await Task.Delay(200, ct);
        }
        using var probe = NewDb(connStr);
        var noteState = await probe.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == noteId, ct);
        var jobs = await probe.IngestJobs.AsNoTracking().Where(j => j.NoteId == noteId).ToListAsync(ct);
        var attachments = await probe.Attachments.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(ct);
        var tasks = jobs.Count > 0
            ? await probe.ExtractionTasks.AsNoTracking().Where(t => t.IngestJobId == jobs[0].Id).ToListAsync(ct)
            : new List<ExtractionTask>();
        var diag = $"note.Status={noteState?.Status} jobs=[{string.Join(",", jobs.Select(j => j.Status))}] atts=[{string.Join(",", attachments.Select(a => a.ExtractionStatus))}] tasks=[{string.Join(",", tasks.Select(t => t.Status))}]";
        throw new TimeoutException($"note {noteId} did not reach terminal status within {TerminalTimeout}; {diag}");
    }

    private static async Task WaitForReadyAfterReprocessAsync(
        string connStr, Guid noteId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TerminalTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var db = NewDb(connStr);
            var jobs = await db.IngestJobs.AsNoTracking()
                .Where(j => j.NoteId == noteId)
                .ToListAsync(ct);
            if (jobs.Count >= 2 && jobs.All(j => IngestJobStatus.IsTerminal(j.Status)))
            {
                return;
            }
            await Task.Delay(200, ct);
        }
        using var probe = NewDb(connStr);
        var note = await probe.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == noteId, ct);
        var allJobs = await probe.IngestJobs.AsNoTracking().Where(j => j.NoteId == noteId).ToListAsync(ct);
        var atts = await probe.Attachments.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(ct);
        var tasks = allJobs.Count > 0
            ? await probe.ExtractionTasks.AsNoTracking().Where(t => allJobs.Select(j => j.Id).Contains(t.IngestJobId)).ToListAsync(ct)
            : new List<ExtractionTask>();
        var diag = $"note.Status={note?.Status} jobs=[{string.Join(",", allJobs.Select(j => j.Status))}] atts=[{string.Join(",", atts.Select(a => a.ExtractionStatus))}] tasks=[{string.Join(",", tasks.Select(t => t.Status))}]";
        throw new TimeoutException($"reprocess for note {noteId} did not complete within {TerminalTimeout}; {diag}");
    }

    private static async Task WaitForDeletedAsync(
        string connStr, Guid noteId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TerminalTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var db = NewDb(connStr);
            var note = await db.Notes.AsNoTracking()
                .SingleOrDefaultAsync(n => n.Id == noteId, ct);
            if (note is not null && note.DeletedAt is not null)
            {
                return;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"note {noteId} did not get tombstoned within {TerminalTimeout}");
    }

    private static string KeyFromUrl(string url) =>
        Uri.UnescapeDataString(url.AsSpan(url.LastIndexOf('/') + 1).ToString());

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "saga-e2e-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        using var db = NewDb(postgres.ConnectionString);
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    private static CloudDbContext NewDb(string connStr)
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connStr, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
