using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.Sync;

[Collection(PostgresCollection.Name)]
public sealed class SyncEndpointsTests(PostgresFixture postgres)
{
    private const string IsoFormat = "O";

    // ---------- /api/sync/pull ----------

    [Fact]
    public async Task SyncPull_surfaces_tombstone_for_soft_deleted_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("composite body", "Inbox/note-A.md");
        await SoftDeleteAsync(noteId);

        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        var item = pull.Items.SingleOrDefault(i => i.NoteId == noteId);
        item.ShouldNotBeNull();
        item!.Deleted.ShouldBeTrue();
        item.Body.ShouldBeEmpty();
        item.Attachments.ShouldBeEmpty();
        item.DeletedAt.ShouldNotBeNull();
        item.RelativePath.ShouldBe("Inbox/note-A.md");
    }

    [Fact]
    public async Task SyncPull_surfaces_projects()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var projectId = await SeedProjectEntityAsync("Project Alpha", aliases: ["alpha"], vaultFolder: "Projects/Alpha");

        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        var p = pull.Projects.SingleOrDefault(x => x.EntityId == projectId);
        p.ShouldNotBeNull();
        p!.CanonicalName.ShouldBe("Project Alpha");
        p.Aliases.ShouldBe(["alpha"]);
        p.VaultFolder.ShouldBe("Projects/Alpha");
        p.IsUserSource.ShouldBeFalse();
        p.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task SyncPull_surfaces_soft_deleted_project_as_tombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var projectId = await SeedProjectEntityAsync("Project Beta");
        await SoftDeleteEntityAsync(projectId);

        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        var p = pull.Projects.SingleOrDefault(x => x.EntityId == projectId);
        p.ShouldNotBeNull();
        p!.DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task SyncPull_advances_next_since_across_notes_and_projects()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("body 1", "Inbox/a.md");
        await Task.Delay(50, ct);
        var projectId = await SeedProjectEntityAsync("Late Project");

        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        pull.NextSince.ShouldNotBeNull();
        var noteAt = pull.Items.Single(i => i.NoteId == noteId).UpdatedAt;
        var projAt = pull.Projects.Single(p => p.EntityId == projectId).UpdatedAt;
        var expected = noteAt > projAt ? noteAt : projAt;
        pull.NextSince!.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task SyncPull_empty_returns_empty_arrays_and_null_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        pull.Items.ShouldBeEmpty();
        pull.Projects.ShouldBeEmpty();
        pull.NextSince.ShouldBeNull();
    }

    // ---------- /api/sync/push ----------

    [Fact]
    public async Task SyncPush_applies_body_returns_200_and_bumps_transition_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("original body", "Inbox/edit-me.md");
        var before = await GetNoteSnapshotAsync(noteId);

        var pushReq = new SyncPushRequest(
            NoteId:        noteId,
            Body:          "user edited body",
            BaseUpdatedAt: before.UpdatedAt.ToDateTimeOffset(),
            Deleted:       false);

        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncPushResponse>(ct);
        body.ShouldNotBeNull();
        body!.NoteId.ShouldBe(noteId);
        body.TransitionVersion.ShouldBe(before.TransitionVersion + 1);

        var after = await GetNoteSnapshotAsync(noteId);
        after.BodyOutput.ShouldBe("user edited body");
        after.BodyHash.ShouldBeNull();
        after.TransitionVersion.ShouldBe(before.TransitionVersion + 1);
        after.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
    }

    [Fact]
    public async Task SyncPush_stale_baseline_returns_409_with_current_values()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("original body", "Inbox/stale.md");
        var staleBaseline = (await GetNoteSnapshotAsync(noteId)).UpdatedAt.ToDateTimeOffset();

        // Simulate a concurrent write: bump updated_at directly in the DB.
        await BumpUpdatedAtAsync(noteId);
        var current = await GetNoteSnapshotAsync(noteId);

        var pushReq = new SyncPushRequest(noteId, "second edit", staleBaseline, false);
        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var conflict = await resp.Content.ReadFromJsonAsync<SyncPushConflict>(ct);
        conflict.ShouldNotBeNull();
        conflict!.Code.ShouldBe(SyncPushConflictCodes.StaleBaseline);
        conflict.CurrentUpdatedAt.ShouldBe(current.UpdatedAt.ToDateTimeOffset());
        conflict.CurrentTransitionVersion.ShouldBe(current.TransitionVersion);
    }

    [Fact]
    public async Task SyncPush_unknown_note_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var pushReq = new SyncPushRequest(
            NoteId:        Guid.NewGuid(),
            Body:          "ghost",
            BaseUpdatedAt: DateTimeOffset.UtcNow,
            Deleted:       false);

        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        payload.GetProperty("code").GetString().ShouldBe(SyncPushConflictCodes.NoteNotFound);
    }

    [Fact]
    public async Task SyncPush_without_bearer_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var pushReq = new SyncPushRequest(Guid.NewGuid(), "x", DateTimeOffset.UtcNow, false);
        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SyncPush_deleted_true_tombstones_and_skips_embed_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("body", "Inbox/zap.md");
        var before = await GetNoteSnapshotAsync(noteId);

        var pushReq = new SyncPushRequest(noteId, string.Empty, before.UpdatedAt.ToDateTimeOffset(), Deleted: true);
        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await GetNoteSnapshotAsync(noteId);
        after.DeletedAt.ShouldNotBeNull();
        after.BodyOutput.ShouldBe("body");

        using var db = NewDb();
        var jobs = await db.IngestJobs.AsNoTracking().Where(j => j.NoteId == noteId).ToListAsync(ct);
        jobs.Any(j => j.Kind == IngestJobKind.UserEditEmbed).ShouldBeFalse();

        // Subsequent pull surfaces tombstone.
        var pull = await PullAsync(client, since: DateTimeOffset.MinValue, ct: ct);
        var item = pull.Items.Single(i => i.NoteId == noteId);
        item.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task SyncPush_enqueues_user_edit_embed_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var noteId = await SeedReadyNoteAsync("body", "Inbox/embed.md");
        var before = await GetNoteSnapshotAsync(noteId);

        var pushReq = new SyncPushRequest(noteId, "user edited body", before.UpdatedAt.ToDateTimeOffset(), false);
        var resp = await client.PostAsJsonAsync("/api/sync/push", pushReq, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var db = NewDb();
        var job = await db.IngestJobs
            .AsNoTracking()
            .Where(j => j.NoteId == noteId && j.Kind == IngestJobKind.UserEditEmbed)
            .SingleOrDefaultAsync(ct);
        job.ShouldNotBeNull();
        job!.Status.ShouldBe(IngestJobStatus.Embedding);

        var eventsLog = job.EventsLog;
        eventsLog.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        var stages = eventsLog.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("stage", out _))
            .Select(e => e.GetProperty("stage").GetString())
            .ToList();
        stages.ShouldContain(SyncPushEndpoint.UserEditPushedStage);
    }

    // ---------- helpers ----------

    private async Task<(CloudApiFactory factory, HttpClient client)> BuildAuthedClientAsync()
    {
        var rawToken = await SeedPluginTokenAsync();
        var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return (factory, client);
    }

    private static async Task<SyncPullResponse> PullAsync(HttpClient client, DateTimeOffset since, CancellationToken ct)
    {
        var url = $"/api/sync/pull?since={Uri.EscapeDataString(since.ToString(IsoFormat))}";
        var resp = await client.GetAsync(url, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncPullResponse>(ct);
        body.ShouldNotBeNull();
        return body!;
    }

    private async Task<Guid> SeedReadyNoteAsync(string body, string relativePath)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        var note = new Note
        {
            Id           = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Ready,
            BodyInput    = body,
            BodyOutput   = body,
            RelativePath = relativePath,
            Tags         = [],
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }

    private async Task<Guid> SeedProjectEntityAsync(
        string canonicalName,
        string[]? aliases = null,
        string? vaultFolder = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        var e = new Entity
        {
            Id            = Guid.CreateVersion7(),
            Kind          = EntityKind.Project,
            CanonicalName = canonicalName,
            Aliases       = aliases ?? [],
            Description   = null,
            Source        = EntitySource.Llm,
            VaultFolder   = vaultFolder,
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        db.Entities.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private async Task SoftDeleteAsync(Guid noteId)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                deleted_at         = {now},
                updated_at         = {now},
                transition_version = transition_version + 1
              WHERE id = {noteId}
            """);
    }

    private async Task SoftDeleteEntityAsync(Guid entityId)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE entities SET
                deleted_at = {now},
                updated_at = {now}
              WHERE id = {entityId}
            """);
    }

    private async Task BumpUpdatedAtAsync(Guid noteId)
    {
        var later = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromSeconds(1));
        using var db = NewDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                updated_at         = {later},
                transition_version = transition_version + 1
              WHERE id = {noteId}
            """);
    }

    private async Task<NoteSnapshot> GetNoteSnapshotAsync(Guid noteId)
    {
        using var db = NewDb();
        var n = await db.Notes.AsNoTracking().SingleAsync(x => x.Id == noteId);
        return new NoteSnapshot(n.UpdatedAt, n.TransitionVersion, n.BodyOutput, n.BodyHash, n.DeletedAt);
    }

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }

    private async Task<string> SeedPluginTokenAsync()
    {
        var raw = "sync-test-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        using var db = NewDb();
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 19, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    private sealed record NoteSnapshot(
        Instant UpdatedAt,
        long TransitionVersion,
        string? BodyOutput,
        string? BodyHash,
        Instant? DeletedAt);
}
