using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Tests.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Specialists;

[Collection(PostgresCollection.Name)]
public sealed class AttachmentExtractionCacheTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Lookup_returns_match_with_same_sha_and_cache_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        const string sha = "abc123";
        const string key = "sha256:ollama:openbmb-minicpm-v4.6-q4_K_M";
        await SeedAttachmentAsync(sha, key, "Description:\nA cat", AttachmentExtractionStatus.Extracted);

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var cache = new AttachmentExtractionCache(db);
        var hit = await cache.LookupAsync(sha, key, ct);

        hit.ShouldNotBeNull();
        hit!.ExtractedText.ShouldBe("Description:\nA cat");
    }

    [Fact]
    public async Task Lookup_with_different_key_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await SeedAttachmentAsync("abc", "sha256:ollama:v1", "x", AttachmentExtractionStatus.Extracted);

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var cache = new AttachmentExtractionCache(db);
        var hit = await cache.LookupAsync("abc", "sha256:ollama:v2", ct);
        hit.ShouldBeNull();
    }

    [Fact]
    public async Task Lookup_ignores_failed_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await SeedAttachmentAsync("abc", "k", "x", AttachmentExtractionStatus.Failed);

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var cache = new AttachmentExtractionCache(db);
        var hit = await cache.LookupAsync("abc", "k", ct);
        hit.ShouldBeNull();
    }

    [Fact]
    public async Task Lookup_with_null_or_whitespace_sha_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var cache = new AttachmentExtractionCache(db);
        (await cache.LookupAsync(null, "k", ct)).ShouldBeNull();
        (await cache.LookupAsync("", "k", ct)).ShouldBeNull();
    }

    private async Task SeedAttachmentAsync(string sha, string cacheKey, string text, string extractionStatus)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Processing,
            BodyInput = "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var att = new Attachment
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            ClientAttachmentId = "a",
            Kind = AttachmentKind.Image,
            StorageProvider = "s3",
            StorageBucket = "test",
            StorageKey = "k",
            Sha256 = sha,
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = extractionStatus,
            ExtractedText = text,
            ExtractionCacheKey = cacheKey,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        db.Attachments.Add(att);
        await db.SaveChangesAsync();
    }
}
