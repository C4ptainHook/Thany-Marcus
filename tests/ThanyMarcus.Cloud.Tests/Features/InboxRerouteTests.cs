using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Folders;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Slow")]
public sealed class InboxRerouteTests(PostgresFixture postgres)
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 6, 1, 12, 0);

    [Fact]
    public async Task Empty_candidate_set_is_a_noop_and_leaves_inbox_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        Guid id;
        await using (var db = NewDb())
        {
            id = SeedInboxNote(db, "n1").Id;
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewDb())
        {
            var result = await NewService(db, RouteTo("Acme")).RunAsync(ct);
            result.AffectedCount.ShouldBe(0);
            result.Desired.ShouldBeEmpty();
        }

        await using var verify = NewDb();
        (await verify.Notes.SingleAsync(n => n.Id == id, ct)).RelativePath.ShouldBe($"Inbox/{id}.md");
    }

    [Fact]
    public async Task Routes_inbox_note_to_chosen_folder_keeping_basename_and_ready_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        Guid id;
        long versionBefore;
        await using (var db = NewDb())
        {
            SeedFolder(db, "Acme");
            var note = SeedInboxNote(db, "body about acme");
            id = note.Id;
            versionBefore = note.TransitionVersion;
            await db.SaveChangesAsync(ct);
        }

        var llm = RouteTo("Acme", confidence: 0.9);
        await using (var db = NewDb())
        {
            var result = await NewService(db, llm).RunAsync(ct);
            result.AffectedCount.ShouldBe(1);
            result.Desired.Single().RelativePath.ShouldBe($"Acme/{id}.md");
        }

        llm.Calls.ShouldContain(c => c.Name == "route" && c.Version == "v1");

        await using var verify = NewDb();
        var moved = await verify.Notes.SingleAsync(n => n.Id == id, ct);
        moved.RelativePath.ShouldBe($"Acme/{id}.md");
        moved.Status.ShouldBe(NoteStatus.Ready);
        moved.BodyOutput.ShouldBe("output");
        moved.TransitionVersion.ShouldBe(versionBefore + 1);
        moved.UpdatedAt.ShouldBeGreaterThan(T0);
    }

    [Fact]
    public async Task Below_threshold_note_is_left_in_inbox_without_a_version_bump()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        Guid id;
        long versionBefore;
        await using (var db = NewDb())
        {
            SeedFolder(db, "Acme");
            var note = SeedInboxNote(db, "weakly related");
            id = note.Id;
            versionBefore = note.TransitionVersion;
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewDb())
        {
            var result = await NewService(db, RouteTo("Acme", confidence: 0.1)).RunAsync(ct);
            result.AffectedCount.ShouldBe(0);
        }

        await using var verify = NewDb();
        var left = await verify.Notes.SingleAsync(n => n.Id == id, ct);
        left.RelativePath.ShouldBe($"Inbox/{id}.md");
        left.TransitionVersion.ShouldBe(versionBefore);
    }

    [Fact]
    public async Task Notes_already_under_a_real_folder_are_never_touched()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        Guid inboxId, filedId;
        await using (var db = NewDb())
        {
            SeedFolder(db, "Acme");
            inboxId = SeedInboxNote(db, "to route").Id;
            filedId = SeedNoteAt(db, "Boats/keep.md").Id;
            await db.SaveChangesAsync(ct);
        }

        await using (var db = NewDb())
        {
            await NewService(db, RouteTo("Acme", confidence: 0.9)).RunAsync(ct);
        }

        await using var verify = NewDb();
        (await verify.Notes.SingleAsync(n => n.Id == inboxId, ct)).RelativePath.ShouldBe($"Acme/{inboxId}.md");
        (await verify.Notes.SingleAsync(n => n.Id == filedId, ct)).RelativePath.ShouldBe("Boats/keep.md");
    }

    [Fact]
    public async Task Batch_is_capped_and_the_remainder_is_left_for_a_later_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using (var db = NewDb())
        {
            SeedFolder(db, "Acme");
            SeedInboxNote(db, "first");
            SeedInboxNote(db, "second");
            await db.SaveChangesAsync(ct);
        }

        var opts = new LlmIntelligenceOptions { RerouteMaxBatch = 1 };
        await using (var db = NewDb())
        {
            var result = await NewService(db, RouteTo("Acme", confidence: 0.9), opts).RunAsync(ct);
            result.AffectedCount.ShouldBe(1);
        }

        await using var verify = NewDb();
        (await verify.Notes.CountAsync(n => n.RelativePath!.StartsWith("Inbox/"), ct)).ShouldBe(1);
        (await verify.Notes.CountAsync(n => n.RelativePath!.StartsWith("Acme/"), ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Registering_first_candidate_folder_pokes_the_signal_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        await using var factory = NoAutoHealFactory();
        using var client = Authed(factory, token);
        var signal = factory.Services.GetRequiredService<InboxRerouteSignal>();
        signal.Reader.TryRead(out _);

        var foldersUri = new Uri("/api/sync/folders", UriKind.Relative);

        (await client.PostAsJsonAsync(foldersUri, new FolderRef("Projects"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        signal.Reader.TryRead(out _).ShouldBeTrue();

        (await client.PostAsJsonAsync(foldersUri, new FolderRef("Archive"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        signal.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task System_and_inbox_folder_registrations_do_not_count_as_candidates()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        await using var factory = NoAutoHealFactory();
        using var client = Authed(factory, token);
        var signal = factory.Services.GetRequiredService<InboxRerouteSignal>();
        signal.Reader.TryRead(out _);

        var foldersUri = new Uri("/api/sync/folders", UriKind.Relative);

        await client.PostAsJsonAsync(foldersUri, new FolderRef("_Private"), ct);
        await client.PostAsJsonAsync(foldersUri, new FolderRef("Inbox"), ct);
        signal.Reader.TryRead(out _).ShouldBeFalse();

        await client.PostAsJsonAsync(foldersUri, new FolderRef("Work"), ct);
        signal.Reader.TryRead(out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Inbox_reroute_endpoint_moves_notes_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedTokenAsync();

        Guid id;
        await using (var db = NewDb())
        {
            SeedFolder(db, "Acme");
            id = SeedInboxNote(db, "body").Id;
            await db.SaveChangesAsync(ct);
        }

        var llm = RouteTo("Acme", confidence: 0.9);
        await using var factory = NoAutoHealFactory(llm);
        using var client = Authed(factory, token);
        var uri = new Uri("/api/sync/inbox/reroute", UriKind.Relative);

        var resp = await client.PostAsync(uri, null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FolderDissolveResponse>(ct);
        body!.AffectedCount.ShouldBe(1);
        body.Desired.Single().RelativePath.ShouldBe($"Acme/{id}.md");

        await using (var v = NewDb())
        {
            (await v.Notes.SingleAsync(n => n.Id == id, ct)).RelativePath.ShouldBe($"Acme/{id}.md");
        }

        var again = await client.PostAsync(uri, null, ct);
        (await again.Content.ReadFromJsonAsync<FolderDissolveResponse>(ct))!.AffectedCount.ShouldBe(0);
    }

    private static InboxRerouteService NewService(
        CloudDbContext db, ConfigurableLlmClient llm, LlmIntelligenceOptions? opts = null) =>
        new(
            db,
            new ConfigurableLlmClientFactory(llm),
            new FolderRouter(db, SystemClock.Instance),
            new LlmEventAppender(db, SystemClock.Instance),
            new StaticOptionsMonitor<LlmIntelligenceOptions>(opts ?? new LlmIntelligenceOptions()),
            SystemClock.Instance,
            NullLogger<InboxRerouteService>.Instance);

    private static ConfigurableLlmClient RouteTo(string folder, double confidence = 0.9) => new()
    {
        RouteResponse = () => new RouteDecisionDto(folder, confidence, "test"),
    };

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }

    private CloudApiFactory NoAutoHealFactory(ConfigurableLlmClient? llm = null) => new()
    {
        ConnectionString = postgres.ConnectionString,
        DisableHostedServices = true,
        CustomizeServices = services =>
        {
            services.RemoveAll<IHostedService>(s => s.ImplementationType == typeof(InboxRerouteWorker));
            if (llm is null) return;
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var t = services[i].ServiceType;
                if (t == typeof(ILlmClient) || t == typeof(ILlmClientFactory)) services.RemoveAt(i);
            }
            services.AddSingleton<ILlmClient>(llm);
            services.AddSingleton<ILlmClientFactory>(new ConfigurableLlmClientFactory(llm));
        },
    };

    private static HttpClient Authed(CloudApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> SeedTokenAsync()
    {
        var raw = "inbox-reroute-" + Guid.NewGuid().ToString("N");
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

    private static Folder SeedFolder(CloudDbContext db, string path)
    {
        var f = new Folder { Path = path, CreatedAt = T0, UpdatedAt = T0 };
        db.Folders.Add(f);
        return f;
    }

    private static Note SeedInboxNote(CloudDbContext db, string bodyInput)
    {
        var id = Guid.CreateVersion7();
        var n = new Note
        {
            Id = id,
            CapturedAt = T0,
            BodyInput = bodyInput,
            BodyOutput = "output",
            Status = NoteStatus.Ready,
            Kind = NoteKind.SynthNote,
            RelativePath = $"Inbox/{id}.md",
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        db.Notes.Add(n);
        return n;
    }

    private static Note SeedNoteAt(CloudDbContext db, string relativePath)
    {
        var n = new Note
        {
            CapturedAt = T0,
            BodyInput = "input",
            BodyOutput = "output",
            Status = NoteStatus.Ready,
            Kind = NoteKind.SynthNote,
            RelativePath = relativePath,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        db.Notes.Add(n);
        return n;
    }
}
