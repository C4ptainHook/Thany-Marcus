using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class ReprocessEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returns_202_and_queues_new_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedReadyNoteAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(new Uri($"/api/notes/{noteId}/reprocess", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var probe = NewDb(postgres.ConnectionString);
        var jobs = await probe.IngestJobs.Where(j => j.NoteId == noteId).ToListAsync(ct);
        jobs.ShouldHaveSingleItem();
        jobs[0].Kind.ShouldBe(IngestJobKind.Reprocess);
        jobs[0].Status.ShouldBe(IngestJobStatus.Queued);
    }

    [Fact]
    public async Task Returns_404_when_note_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(new Uri($"/api/notes/{Guid.NewGuid()}/reprocess", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_404_when_note_tombstoned()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedReadyNoteAsync(postgres, deleted: true);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(new Uri($"/api/notes/{noteId}/reprocess", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_409_when_active_job_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedReadyNoteAsync(postgres);

        using (var seedDb = NewDb(postgres.ConnectionString))
        {
            var now = SystemClock.Instance.GetCurrentInstant();
            seedDb.IngestJobs.Add(new IngestJob
            {
                Id = Guid.CreateVersion7(),
                NoteId = noteId,
                Status = IngestJobStatus.ExtractingAttachments,
                ScheduledAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await seedDb.SaveChangesAsync(ct);
        }

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(new Uri($"/api/notes/{noteId}/reprocess", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resets_attachments_to_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedReadyNoteAsync(postgres);

        Guid attId;
        using (var seedDb = NewDb(postgres.ConnectionString))
        {
            var now = SystemClock.Instance.GetCurrentInstant();
            var att = new Attachment
            {
                Id = Guid.CreateVersion7(),
                NoteId = noteId,
                ClientAttachmentId = "a1",
                Kind = AttachmentKind.Image,
                StorageProvider = "s3",
                StorageBucket = "test",
                StorageKey = "k",
                Status = AttachmentStatus.Uploaded,
                ExtractionStatus = AttachmentExtractionStatus.Extracted,
                ExtractedText = "old",
                Extra = JsonDocument.Parse("{}"),
                CreatedAt = now,
                UpdatedAt = now,
            };
            seedDb.Attachments.Add(att);
            await seedDb.SaveChangesAsync(ct);
            attId = att.Id;
        }

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(new Uri($"/api/notes/{noteId}/reprocess", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var probe = NewDb(postgres.ConnectionString);
        var att2 = await probe.Attachments.SingleAsync(a => a.Id == attId, ct);
        att2.ExtractionStatus.ShouldBe(AttachmentExtractionStatus.Pending);
        att2.ExtractedText.ShouldBeNull();
    }

    private static async Task<Guid> SeedReadyNoteAsync(PostgresFixture postgres, bool deleted = false)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "body",
            DeletedAt = deleted ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "rp-" + Guid.NewGuid().ToString("N");
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
