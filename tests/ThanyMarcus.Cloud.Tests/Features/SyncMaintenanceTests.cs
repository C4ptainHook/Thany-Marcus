using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Slow")]
public sealed class SyncMaintenanceTests(PostgresFixture postgres)
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 6, 4, 12, 0);
    private static readonly string[] ExpectedReroutePaths = ["Inbox/one.md", "Inbox/two.md"];

    [Fact]
    public async Task Tombstone_cleans_mentions_decrements_entity_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var db = NewDb();

        var entity = SeedEntity(db, mentionCount: 3);
        var note = SeedNote(db, status: NoteStatus.Ready, relativePath: "Inbox/a.md");
        db.Mentions.Add(NewMention(entity.Id, note.Id));
        db.Mentions.Add(NewMention(entity.Id, note.Id));
        await db.SaveChangesAsync(ct);

        var svc = new NoteTombstoneService(db, new FakeClock(T0));

        (await svc.TombstoneAsync(note.Id, ct)).ShouldBe(TombstoneOutcome.Tombstoned);
        (await svc.TombstoneAsync(note.Id, ct)).ShouldBe(TombstoneOutcome.AlreadyTombstoned);

        await using var verify = NewDb();
        (await verify.Notes.SingleAsync(n => n.Id == note.Id, ct)).DeletedAt.ShouldNotBeNull();
        (await verify.Mentions.CountAsync(m => m.NoteId == note.Id, ct)).ShouldBe(0);
        (await verify.Entities.SingleAsync(e => e.Id == entity.Id, ct)).MentionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Tombstone_of_hub_note_suppresses_entity_and_revive_restores_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var db = NewDb();

        var entity = SeedEntity(db, mentionCount: 5);
        var hub = SeedNote(db, status: NoteStatus.Ready, relativePath: "Entities/person/X.md");
        hub.IsHub = true;
        hub.HubEntityId = entity.Id;
        entity.HubNoteId = hub.Id;
        await db.SaveChangesAsync(ct);

        var svc = new NoteTombstoneService(db, new FakeClock(T0));
        await svc.TombstoneAsync(hub.Id, ct);

        await using (var v = NewDb())
        {
            var e = await v.Entities.SingleAsync(x => x.Id == entity.Id, ct);
            e.HubSuppressed.ShouldBeTrue();
            e.HubNoteId.ShouldBeNull();
        }

        (await svc.ReviveAsync(hub.Id, ct)).ShouldBe(ReviveOutcome.Revived);

        await using (var v = NewDb())
        {
            (await v.Notes.SingleAsync(n => n.Id == hub.Id, ct)).DeletedAt.ShouldBeNull();
            var e = await v.Entities.SingleAsync(x => x.Id == entity.Id, ct);
            e.HubSuppressed.ShouldBeFalse();
            e.HubNoteId.ShouldBe(hub.Id);
        }
    }

    [Fact]
    public async Task Revive_unknown_note_returns_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var db = NewDb();
        var svc = new NoteTombstoneService(db, new FakeClock(T0));
        (await svc.ReviveAsync(Guid.CreateVersion7(), ct)).ShouldBe(ReviveOutcome.NotFound);
    }

    [Fact]
    public async Task Gc_hard_deletes_tombstones_past_retention_window()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var db = NewDb();
        var fresh = SeedNote(db, status: NoteStatus.Ready, relativePath: "Inbox/fresh.md");
        fresh.DeletedAt = T0.Minus(Duration.FromDays(2));
        var expired = SeedNote(db, status: NoteStatus.Ready, relativePath: "Inbox/old.md");
        expired.DeletedAt = T0.Minus(Duration.FromDays(20));
        await db.SaveChangesAsync(ct);

        await using var factory = NewFactory();
        var sweeper = new TombstoneGcSweeper(
            factory.Services, new FakeClock(T0), NullLogger<TombstoneGcSweeper>.Instance);
        await sweeper.SweepOnceAsync(ct);

        await using var verify = NewDb();
        (await verify.Notes.AnyAsync(n => n.Id == expired.Id, ct)).ShouldBeFalse();
        (await verify.Notes.AnyAsync(n => n.Id == fresh.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_endpoint_tombstones_and_status_endpoint_reports_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        Guid liveId, deletedId;
        await using (var db = NewDb())
        {
            liveId = SeedNote(db, status: NoteStatus.Ready, relativePath: "Inbox/live.md").Id;
            deletedId = SeedNote(db, status: NoteStatus.Ready, relativePath: "Inbox/gone.md").Id;
            await db.SaveChangesAsync(ct);
        }

        await using var factory = NewFactory();
        using var client = Authed(factory, token);

        var del = await client.DeleteAsync(new Uri($"/api/sync/notes/{deletedId}", UriKind.Relative), ct);
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // idempotent
        (await client.DeleteAsync(new Uri($"/api/sync/notes/{deletedId}", UriKind.Relative), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var status = await client.GetFromJsonAsync<SyncStatusResponse>(
            new Uri($"/api/sync/status?notes={liveId},{deletedId}", UriKind.Relative), ct);
        status.ShouldNotBeNull();
        status!.Items.Single(i => i.NoteId == liveId).Status.ShouldBe(NoteStatus.Ready);
        status.Items.Single(i => i.NoteId == deletedId).Status.ShouldBe("deleted");

        var reviveResp = await client.PostAsync(
            new Uri($"/api/sync/notes/{deletedId}/revive", UriKind.Relative), null, ct);
        reviveResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reviveResp.Content.ReadFromJsonAsync<SyncReviveResponse>(ct))!.Revived.ShouldBeTrue();
    }

    [Fact]
    public async Task Folder_dissolve_reroute_moves_notes_to_inbox_and_force_delete_tombstones()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        Guid n1, n2, keep;
        await using (var db = NewDb())
        {
            n1 = SeedNote(db, status: NoteStatus.Ready, relativePath: "Cars/one.md").Id;
            n2 = SeedNote(db, status: NoteStatus.Ready, relativePath: "Cars/two.md").Id;
            keep = SeedNote(db, status: NoteStatus.Ready, relativePath: "Boats/keep.md").Id;
            await db.SaveChangesAsync(ct);
        }

        await using var factory = NewFactory();
        using var client = Authed(factory, token);

        var reroute = await client.PostAsJsonAsync(
            new Uri("/api/sync/folders/dissolve", UriKind.Relative),
            new FolderDissolveRequest("Cars", FolderDissolveMode.Reroute), ct);
        reroute.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rr = await reroute.Content.ReadFromJsonAsync<FolderDissolveResponse>(ct);
        rr!.AffectedCount.ShouldBe(2);
        rr.Desired.Select(d => d.RelativePath).OrderBy(p => p)
            .ShouldBe(ExpectedReroutePaths);

        await using (var v = NewDb())
        {
            (await v.Notes.SingleAsync(n => n.Id == n1, ct)).RelativePath.ShouldBe("Inbox/one.md");
            (await v.Notes.SingleAsync(n => n.Id == keep, ct)).RelativePath.ShouldBe("Boats/keep.md");
        }

        var force = await client.PostAsJsonAsync(
            new Uri("/api/sync/folders/dissolve", UriKind.Relative),
            new FolderDissolveRequest("Boats", FolderDissolveMode.ForceDelete), ct);
        force.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await force.Content.ReadFromJsonAsync<FolderDissolveResponse>(ct))!.AffectedCount.ShouldBe(1);

        await using (var v = NewDb())
        {
            (await v.Notes.SingleAsync(n => n.Id == keep, ct)).DeletedAt.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Move_updates_relative_path_without_bumping_updated_at()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        Guid id;
        Instant before;
        await using (var db = NewDb())
        {
            var note = SeedNote(db, status: NoteStatus.Ready, relativePath: "Cars/a.md");
            id = note.Id;
            before = note.UpdatedAt;
            await db.SaveChangesAsync(ct);
        }

        await using var factory = NewFactory();
        using var client = Authed(factory, token);

        var resp = await client.PostAsJsonAsync(
            new Uri($"/api/sync/notes/{id}/move", UriKind.Relative),
            new SyncMoveRequest("Inbox/a.md"), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var v = NewDb();
        var moved = await v.Notes.SingleAsync(n => n.Id == id, ct);
        moved.RelativePath.ShouldBe("Inbox/a.md");
        moved.UpdatedAt.ShouldBe(before);
    }

    [Fact]
    public async Task Folder_registry_register_list_unregister_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        await using var factory = NewFactory();
        using var client = Authed(factory, token);
        var foldersUri = new Uri("/api/sync/folders", UriKind.Relative);

        var reg = await client.PostAsJsonAsync(foldersUri, new FolderRef("Projects/Foo"), ct);
        reg.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // register-existing is a no-op
        (await client.PostAsJsonAsync(foldersUri, new FolderRef("Projects/Foo"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var listed = await client.GetFromJsonAsync<FolderListResponse>(foldersUri, ct);
        listed!.Folders.ShouldContain("Projects/Foo");

        var del = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, foldersUri)
            { Content = JsonContent.Create(new FolderRef("Projects/Foo")) }, ct);
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // unregister-absent is a no-op
        var delAgain = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, foldersUri)
            { Content = JsonContent.Create(new FolderRef("Projects/Foo")) }, ct);
        delAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var afterDelete = await client.GetFromJsonAsync<FolderListResponse>(foldersUri, ct);
        afterDelete!.Folders.ShouldNotContain("Projects/Foo");

        // re-register revives the soft-deleted row rather than violating the unique index
        (await client.PostAsJsonAsync(foldersUri, new FolderRef("Projects/Foo"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var revived = await client.GetFromJsonAsync<FolderListResponse>(foldersUri, ct);
        revived!.Folders.ShouldContain("Projects/Foo");
    }

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }

    private CloudApiFactory NewFactory() => new()
    {
        ConnectionString = postgres.ConnectionString,
        DisableHostedServices = true,
    };

    private static HttpClient Authed(CloudApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> SeedTokenAsync()
    {
        var raw = "sync-maint-" + Guid.NewGuid().ToString("N");
        await using var db = NewDb();
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(raw)),
            Label = "test",
            CreatedAt = T0,
        });
        await db.SaveChangesAsync();
        return raw;
    }

    private static Entity SeedEntity(CloudDbContext db, int mentionCount)
    {
        var e = new Entity
        {
            Kind = EntityKind.Person,
            CanonicalName = "Person " + Guid.NewGuid().ToString("N")[..8],
            Source = EntitySource.Llm,
            MentionCount = mentionCount,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        db.Entities.Add(e);
        return e;
    }

    private static Note SeedNote(CloudDbContext db, string status, string relativePath)
    {
        var n = new Note
        {
            CapturedAt = T0,
            BodyInput = "input",
            BodyOutput = "output",
            Status = status,
            Kind = NoteKind.SynthNote,
            RelativePath = relativePath,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        db.Notes.Add(n);
        return n;
    }

    private static Mention NewMention(Guid entityId, Guid noteId) => new()
    {
        EntityId = entityId,
        NoteId = noteId,
        AnchorText = "x",
        StartOffset = 0,
        EndOffset = 1,
        Confidence = 0.9f,
        CreatedAt = T0,
    };
}
