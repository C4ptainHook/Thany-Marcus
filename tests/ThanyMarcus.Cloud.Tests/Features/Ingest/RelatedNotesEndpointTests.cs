using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;
using Microsoft.EntityFrameworkCore;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class RelatedNotesEndpointTests(PostgresFixture postgres)
{
    private const string BodyDraft = "This is a draft that is well over thirty characters long and worth embedding.";

    [Fact]
    public async Task Body_path_embeds_and_returns_results()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        await SeedRelatedNoteAsync(postgres, FakeEmbeddingClient.DeterministicUnitVector(BodyDraft), hoursAgo: 24);

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CustomizeServices = svc =>
            {
                for (var i = svc.Count - 1; i >= 0; i--)
                {
                    if (svc[i].ServiceType == typeof(IEmbeddingClient)) svc.RemoveAt(i);
                }
                svc.AddSingleton<IEmbeddingClient>(fake);
            },
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { body = BodyDraft, k = 5 },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<RelatedNotesEndpoint.RelatedNotesResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.Items.Count.ShouldBe(1);
        fake.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Body_path_honors_exclude_note_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var queryVec = FakeEmbeddingClient.DeterministicUnitVector(BodyDraft);
        var excludedId = await SeedRelatedNoteAsync(postgres, queryVec, hoursAgo: 24);
        var keptId = await SeedRelatedNoteAsync(postgres, queryVec, hoursAgo: 24);

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CustomizeServices = svc =>
            {
                for (var i = svc.Count - 1; i >= 0; i--)
                {
                    if (svc[i].ServiceType == typeof(IEmbeddingClient)) svc.RemoveAt(i);
                }
                svc.AddSingleton<IEmbeddingClient>(fake);
            },
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { body = BodyDraft, k = 5, excludeNoteId = excludedId },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<RelatedNotesEndpoint.RelatedNotesResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.Items.ShouldNotContain(i => i.Id == excludedId);
        payload.Items.ShouldContain(i => i.Id == keptId);
    }

    [Fact]
    public async Task NoteId_path_uses_stored_embedding_no_embed_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var anchor = FakeEmbeddingClient.DeterministicUnitVector("anchor");
        var anchorId = await SeedRelatedNoteAsync(postgres, anchor, hoursAgo: 24);
        await SeedRelatedNoteAsync(postgres, FakeEmbeddingClient.DeterministicUnitVector("other"), hoursAgo: 24);

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CustomizeServices = svc =>
            {
                for (var i = svc.Count - 1; i >= 0; i--)
                {
                    if (svc[i].ServiceType == typeof(IEmbeddingClient)) svc.RemoveAt(i);
                }
                svc.AddSingleton<IEmbeddingClient>(fake);
            },
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { noteId = anchorId, k = 5 },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<RelatedNotesEndpoint.RelatedNotesResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.Items.ShouldNotContain(i => i.Id == anchorId);
        fake.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task NoteId_404_when_note_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { noteId = Guid.NewGuid() },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NoteId_404_when_embedding_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        var noteId = Guid.CreateVersion7();
        var now = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(24);
        using (var db = NewDb(postgres.ConnectionString))
        {
            db.Notes.Add(new Note
            {
                Id = noteId,
                ClientNoteId = Guid.NewGuid().ToString(),
                CapturedAt = now,
                Status = NoteStatus.Ready,
                BodyInput = "body",
                Embedding = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { noteId },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Body_too_short_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { body = "too short" },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Both_body_and_noteId_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { body = BodyDraft, noteId = Guid.NewGuid() },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/notes/related",
            new { body = BodyDraft },
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExtractTitle_uses_h1_when_present()
    {
        var t = RelatedNotesEndpoint.ExtractTitle(
            "---\ntags: x\n---\n# Hello World\n\nBody text.",
            "Inbox/anything.md");
        t.ShouldBe("Hello World");
    }

    [Fact]
    public async Task ExtractTitle_falls_back_to_filename()
    {
        var t = RelatedNotesEndpoint.ExtractTitle(
            "Body only, no heading.",
            "Inbox/my-note-12345678.md");
        t.ShouldBe("my-note-12345678");
    }

    [Fact]
    public async Task ExtractSnippet_strips_frontmatter_and_headings()
    {
        var s = RelatedNotesEndpoint.ExtractSnippet(
            "---\ntags: x\n---\n# Title\n\nFirst body paragraph here.");
        s.ShouldBe("First body paragraph here.");
    }

    private static async Task<Guid> SeedRelatedNoteAsync(PostgresFixture postgres, float[] vec, int hoursAgo)
    {
        var now = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(hoursAgo);
        var id = Guid.CreateVersion7();
        using var db = NewDb(postgres.ConnectionString);
        db.Notes.Add(new Note
        {
            Id = id,
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "input",
            BodyOutput = "# Sample\n\nA paragraph.",
            RelativePath = $"Inbox/sample-{id:N}.md",
            Embedding = new Vector(vec),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "rel-" + Guid.NewGuid().ToString("N");
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
