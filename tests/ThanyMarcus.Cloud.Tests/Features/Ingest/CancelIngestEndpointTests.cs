using System.Net;
using System.Net.Http.Headers;
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

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class CancelIngestEndpointTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(IngestJobStatus.Queued)]
    [InlineData(IngestJobStatus.ExtractingAttachments)]
    [InlineData(IngestJobStatus.Composing)]
    [InlineData(IngestJobStatus.Routing)]
    [InlineData(IngestJobStatus.ExtractingEntities)]
    [InlineData(IngestJobStatus.Synthesizing)]
    [InlineData(IngestJobStatus.Embedding)]
    public async Task Cancel_from_non_terminal_status_returns_204_and_cleans_up(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new FakeArtifactStore();
        var (noteId, jobId, attKey) = await SeedNoteJobAndAttachmentAsync(postgres, store, status);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var probe = NewDb(postgres.ConnectionString);
        (await probe.Notes.AnyAsync(n => n.Id == noteId, ct)).ShouldBeFalse();
        (await probe.Attachments.AnyAsync(a => a.NoteId == noteId, ct)).ShouldBeFalse();
        (await probe.IngestJobs.AnyAsync(j => j.Id == jobId, ct)).ShouldBeFalse();
        store.Objects.ContainsKey(attKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_after_succeeded_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new FakeArtifactStore();
        var (noteId, _, _) = await SeedNoteJobAndAttachmentAsync(
            postgres, store, IngestJobStatus.Succeeded);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var probe = NewDb(postgres.ConnectionString);
        (await probe.Notes.AnyAsync(n => n.Id == noteId, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_after_failed_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new FakeArtifactStore();
        var (noteId, _, _) = await SeedNoteJobAndAttachmentAsync(
            postgres, store, IngestJobStatus.FailedSynthesis);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Second_cancel_returns_404_after_cleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new FakeArtifactStore();
        var (noteId, _, _) = await SeedNoteJobAndAttachmentAsync(
            postgres, store, IngestJobStatus.Routing);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_404_when_note_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new FakeArtifactStore();

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{Guid.NewGuid()}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var store = new FakeArtifactStore();
        var (noteId, _, _) = await SeedNoteJobAndAttachmentAsync(
            postgres, store, IngestJobStatus.Queued);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Spaces_delete_failure_still_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var store = new ThrowingDeleteStore();
        var (noteId, _, _) = await SeedNoteJobAndAttachmentAsync(
            postgres, key => store.Seed(key, 1024, "etag", "image/jpeg"),
            IngestJobStatus.Synthesizing);

        await using var factory = NewFactory(postgres.ConnectionString, store);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            new Uri($"/api/ingest/{noteId}/cancel", UriKind.Relative), null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var probe = NewDb(postgres.ConnectionString);
        (await probe.Notes.AnyAsync(n => n.Id == noteId, ct)).ShouldBeFalse();
    }

    private static Task<(Guid noteId, Guid jobId, string storageKey)> SeedNoteJobAndAttachmentAsync(
        PostgresFixture postgres, FakeArtifactStore store, string jobStatus) =>
        SeedNoteJobAndAttachmentAsync(
            postgres, key => store.Seed(key, 1024, "etag", "image/jpeg"), jobStatus);

    private static async Task<(Guid noteId, Guid jobId, string storageKey)> SeedNoteJobAndAttachmentAsync(
        PostgresFixture postgres, Action<string> seedStore, string jobStatus)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = jobStatus == IngestJobStatus.Succeeded
                ? NoteStatus.Ready
                : NoteStatus.Processing,
            BodyInput = "body",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);

        var att = new Attachment
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            ClientAttachmentId = "a1",
            Kind = AttachmentKind.Image,
            StorageProvider = "s3",
            StorageBucket = "test",
            StorageKey = $"notes/{note.Id}/a1.jpg",
            Status = AttachmentStatus.Uploaded,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Attachments.Add(att);
        seedStore(att.StorageKey);

        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            Status = jobStatus,
            ScheduledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.IngestJobs.Add(job);

        await db.SaveChangesAsync();
        return (note.Id, job.Id, att.StorageKey);
    }

    private static CloudApiFactory NewFactory(string connStr, IArtifactStore store) =>
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
        var raw = "cx-" + Guid.NewGuid().ToString("N");
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

    private sealed class ThrowingDeleteStore : IArtifactStore
    {
        private readonly FakeArtifactStore inner = new();

        public void Seed(string key, long byteSize, string etag = "fake-etag", string? mimeType = null) =>
            inner.Seed(key, byteSize, etag, mimeType);

        public Task<PresignedUpload> IssueUploadUrlAsync(
            string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct) =>
            inner.IssueUploadUrlAsync(key, mimeType, byteSize, ttl, ct);

        public Task<PresignedDownload> IssueDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct) =>
            inner.IssueDownloadUrlAsync(key, ttl, ct);

        public Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct) =>
            inner.HeadAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("simulated spaces 5xx");

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            inner.OpenReadAsync(key, ct);

        public Task UploadBytesAsync(
            string key, byte[] bytes, string mimeType, bool finalized, CancellationToken ct) =>
            inner.UploadBytesAsync(key, bytes, mimeType, finalized, ct);
    }
}
