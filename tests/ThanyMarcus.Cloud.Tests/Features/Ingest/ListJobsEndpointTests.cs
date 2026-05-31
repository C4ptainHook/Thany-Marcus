using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public sealed class ListJobsEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returns_401_without_plugin_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/api/ingest/jobs?status=active", UriKind.Relative), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Returns_empty_when_database_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active&include=recent", ct);

        body.ShouldNotBeNull();
        body.Active.ShouldBeEmpty();
        body.Recent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Active_filter_omits_terminal_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var now = SystemClock.Instance.GetCurrentInstant();
        var activeNoteId = await SeedJobAsync(postgres, IngestJobStatus.Synthesizing,
            bodyInput: "active body", createdAt: now);
        await SeedJobAsync(postgres, IngestJobStatus.Succeeded,
            bodyInput: "done body", createdAt: now - Duration.FromMinutes(1));
        await SeedJobAsync(postgres, IngestJobStatus.FailedSynthesis,
            bodyInput: "failed body", createdAt: now - Duration.FromMinutes(2));

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active", ct);

        body.ShouldNotBeNull();
        body.Active.Count.ShouldBe(1);
        body.Active[0].NoteId.ShouldBe(activeNoteId);
        body.Active[0].Status.ShouldBe(IngestJobStatus.Synthesizing);
        body.Recent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recent_returns_top_10_by_updated_at_desc()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var baseTime = SystemClock.Instance.GetCurrentInstant();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await SeedJobAsync(
                postgres,
                IngestJobStatus.Succeeded,
                bodyInput: $"note {i}",
                createdAt: baseTime - Duration.FromMinutes(20 - i),
                updatedAt: baseTime - Duration.FromMinutes(20 - i)));
        }

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active&include=recent", ct);

        body.ShouldNotBeNull();
        body.Recent.Count.ShouldBe(10);
        // The 10 most recent are ids[2]..ids[11] (highest updated_at)
        body.Recent[0].NoteId.ShouldBe(ids[11]);
        body.Recent[9].NoteId.ShouldBe(ids[2]);
    }

    [Fact]
    public async Task Title_comes_from_H1_when_body_output_present()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var bodyOutput = "---\nkey: val\n---\n\n# My Cool Title\n\nbody text";
        await SeedJobAsync(postgres, IngestJobStatus.Synthesizing,
            bodyInput: "input", bodyOutput: bodyOutput);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active", ct);

        body!.Active[0].Title.ShouldBe("My Cool Title");
    }

    [Fact]
    public async Task Title_falls_back_to_body_prefix()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var input = new string('a', 80);
        await SeedJobAsync(postgres, IngestJobStatus.Queued, bodyInput: input);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active", ct);

        body!.Active[0].Title.Length.ShouldBe(60);
    }

    [Fact]
    public async Task Title_is_untitled_when_both_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await SeedJobAsync(postgres, IngestJobStatus.Queued, bodyInput: "");

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active", ct);

        body!.Active[0].Title.ShouldBe("(untitled)");
    }

    [Fact]
    public async Task ExtractionFailures_excludes_extracted_minimal()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var noteId = await SeedJobAsync(postgres, IngestJobStatus.Synthesizing, bodyInput: "x");

        using (var seedDb = NewDb(postgres.ConnectionString))
        {
            var now = SystemClock.Instance.GetCurrentInstant();
            seedDb.Attachments.Add(new Attachment
            {
                Id = Guid.CreateVersion7(),
                NoteId = noteId,
                ClientAttachmentId = "a1",
                Kind = AttachmentKind.Image,
                StorageProvider = "s3",
                StorageBucket = "test",
                StorageKey = "k1",
                Status = AttachmentStatus.Uploaded,
                ExtractionStatus = AttachmentExtractionStatus.Failed,
                ExtractionError = "vlm failed",
                Extra = JsonDocument.Parse("{}"),
                CreatedAt = now,
                UpdatedAt = now,
            });
            seedDb.Attachments.Add(new Attachment
            {
                Id = Guid.CreateVersion7(),
                NoteId = noteId,
                ClientAttachmentId = "a2",
                Kind = AttachmentKind.Url,
                StorageProvider = "s3",
                StorageBucket = "test",
                StorageKey = "k2",
                Status = AttachmentStatus.Uploaded,
                ExtractionStatus = AttachmentExtractionStatus.ExtractedMinimal,
                Extra = JsonDocument.Parse("{}"),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await seedDb.SaveChangesAsync(ct);
        }

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var body = await client.GetFromJsonAsync<ListJobsEndpoint.ListJobsResponse>(
            "/api/ingest/jobs?status=active", ct);

        var item = body!.Active.Single();
        item.ExtractionFailures.Count.ShouldBe(1);
        item.ExtractionFailures[0].Kind.ShouldBe(AttachmentKind.Image);
        item.ExtractionFailures[0].Reason.ShouldBe("vlm failed");
    }

    [Fact]
    public async Task Unsupported_status_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = AuthedClient(factory, token);

        var resp = await client.GetAsync(new Uri("/api/ingest/jobs?status=bogus", UriKind.Relative), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static HttpClient AuthedClient(CloudApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> SeedJobAsync(
        PostgresFixture postgres,
        string jobStatus,
        string bodyInput,
        string? bodyOutput = null,
        Instant? createdAt = null,
        Instant? updatedAt = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var created = createdAt ?? now;
        var updated = updatedAt ?? created;

        using var db = NewDb(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = created,
            Status = NoteStatus.Processing,
            BodyInput = bodyInput,
            BodyOutput = bodyOutput,
            CreatedAt = created,
            UpdatedAt = updated,
        };
        db.Notes.Add(note);

        db.IngestJobs.Add(new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            Status = jobStatus,
            ScheduledAt = created,
            CreatedAt = created,
            UpdatedAt = updated,
        });

        await db.SaveChangesAsync();
        return note.Id;
    }

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "lj-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        using var db = NewDb(postgres.ConnectionString);
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 31, 0, 0),
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
