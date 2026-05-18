using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class IngestEndpointsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Init_returns_presigned_url_for_binary_attachment_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        var fakeStore = new FakeArtifactStore();
        await using var factory = NewFactory(postgres.ConnectionString, fakeStore);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rawToken);

        var req = new IngestInitRequest(
            ClientNoteId: Guid.NewGuid().ToString(),
            CapturedAt:   DateTimeOffset.UtcNow,
            Body:         "Reading Berlin trains",
            Attachments:
            [
                new("a-url", AttachmentKind.Url, null, null, null, null,
                    System.Text.Json.JsonDocument.Parse("{\"url\":\"https://example.com\"}").RootElement),
                new("a-voice", AttachmentKind.Voice, "audio/wav", 1024L, "abc123", "memo.wav",
                    System.Text.Json.JsonDocument.Parse("{}").RootElement),
            ]);

        var resp = await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IngestInitResponse>(ct);
        body.ShouldNotBeNull();
        body.Uploads.Count.ShouldBe(1);
        body.Uploads[0].ClientAttachmentId.ShouldBe("a-voice");
        body.Uploads[0].UploadUrl.ShouldStartWith("https://fake.example.test/upload/");
    }

    [Fact]
    public async Task Init_is_idempotent_on_clientNoteId()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        var fakeStore = new FakeArtifactStore();
        await using var factory = NewFactory(postgres.ConnectionString, fakeStore);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rawToken);

        var clientNoteId = Guid.NewGuid().ToString();
        var req = new IngestInitRequest(clientNoteId, DateTimeOffset.UtcNow, "body",
            [
                new("a1", AttachmentKind.Voice, "audio/wav", 100L, "sha", null,
                    System.Text.Json.JsonDocument.Parse("{}").RootElement),
            ]);

        var first  = await (await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), req, ct))
                          .Content.ReadFromJsonAsync<IngestInitResponse>(ct);
        var second = await (await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), req, ct))
                          .Content.ReadFromJsonAsync<IngestInitResponse>(ct);

        first!.NoteId.ShouldBe(second!.NoteId);
        first.Uploads[0].AttachmentId.ShouldBe(second.Uploads[0].AttachmentId);
    }

    [Fact]
    public async Task Init_without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var fakeStore = new FakeArtifactStore();
        await using var factory = NewFactory(postgres.ConnectionString, fakeStore);
        using var client = factory.CreateClient();

        var req = new IngestInitRequest("cn", DateTimeOffset.UtcNow, "body", []);
        var resp = await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Finalize_with_matching_byte_size_flips_note_to_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        var fakeStore = new FakeArtifactStore();
        await using var factory = NewFactory(postgres.ConnectionString, fakeStore);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rawToken);

        var initReq = new IngestInitRequest(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "body",
            [
                new("a1", AttachmentKind.Voice, "audio/wav", 1024L, "sha", null,
                    System.Text.Json.JsonDocument.Parse("{}").RootElement),
            ]);
        var initResp = await (await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), initReq, ct))
                            .Content.ReadFromJsonAsync<IngestInitResponse>(ct);
        var upload = initResp!.Uploads[0];

        fakeStore.Seed(KeyFromUrl(upload.UploadUrl), byteSize: 1024);

        var finalizeReq = new IngestFinalizeRequest(
            [new IngestFinalizeUploadedAttachment(upload.AttachmentId, "sha", 1024L)]);
        var finalizeResp = await client.PostAsJsonAsync(
            new Uri($"/api/ingest/{initResp.NoteId}/finalize", UriKind.Relative), finalizeReq, ct);

        finalizeResp.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var note = await db.Notes.SingleAsync(n => n.Id == initResp.NoteId, ct);
        note.Status.ShouldBe(NoteStatus.Processing);

        var jobCount = await db.IngestJobs.CountAsync(j => j.NoteId == note.Id, ct);
        jobCount.ShouldBe(1);
    }

    [Fact]
    public async Task Finalize_with_byte_size_mismatch_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        var fakeStore = new FakeArtifactStore();
        await using var factory = NewFactory(postgres.ConnectionString, fakeStore);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rawToken);

        var initReq = new IngestInitRequest(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "body",
            [
                new("a1", AttachmentKind.Voice, "audio/wav", 1024L, "sha", null,
                    System.Text.Json.JsonDocument.Parse("{}").RootElement),
            ]);
        var initResp = await (await client.PostAsJsonAsync(new Uri("/api/ingest/init", UriKind.Relative), initReq, ct))
                            .Content.ReadFromJsonAsync<IngestInitResponse>(ct);
        var upload = initResp!.Uploads[0];

        fakeStore.Seed(KeyFromUrl(upload.UploadUrl), byteSize: 2048);

        var finalizeReq = new IngestFinalizeRequest(
            [new IngestFinalizeUploadedAttachment(upload.AttachmentId, "sha", 1024L)]);
        var finalizeResp = await client.PostAsJsonAsync(
            new Uri($"/api/ingest/{initResp.NoteId}/finalize", UriKind.Relative), finalizeReq, ct);

        finalizeResp.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static string KeyFromUrl(string url) =>
        Uri.UnescapeDataString(url.AsSpan(url.LastIndexOf('/') + 1).ToString());

    private static CloudApiFactory NewFactory(string connStr, FakeArtifactStore store) =>
        new CloudApiFactory
        {
            ConnectionString = connStr,
            CustomizeServices = services =>
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    if (services[i].ServiceType == typeof(IArtifactStore))
                    {
                        services.RemoveAt(i);
                    }
                }
                services.AddSingleton<IArtifactStore>(store);
            },
        };

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "test-bearer-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new CloudDbContext(opts);
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }
}

