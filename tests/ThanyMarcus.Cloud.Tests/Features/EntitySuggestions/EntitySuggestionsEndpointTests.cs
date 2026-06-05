using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Tests.Features.EntitySuggestions;

[Collection(PostgresCollection.Name)]
public sealed class EntitySuggestionsEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task List_respects_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var surfaceable = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2);
        await SeedSuggestionAsync("Quincy Jones", occurrenceCount: 1, distinctNoteCount: 1);

        var resp = await client.GetAsync("/api/entity-suggestions", ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ListEntitySuggestionsResponse>(ct);
        body.ShouldNotBeNull();
        body!.Suggestions.Count.ShouldBe(1);
        body.Suggestions[0].Id.ShouldBe(surfaceable);
        body.Suggestions[0].CanonicalText.ShouldBe("Michael Jackson");
        body.Suggestions[0].SampleOccurrence.ShouldNotBeNull();
    }

    [Fact]
    public async Task Accept_creates_entity_stub_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var id = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2,
            aliases: ["Mike"]);

        var resp = await client.PostAsync($"/api/entity-suggestions/{id}/accept", null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await resp.Content.ReadFromJsonAsync<AcceptEntitySuggestionResponse>(ct);
        accepted.ShouldNotBeNull();
        var entityId = accepted!.EntityId;

        using (var db = NewDb())
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            entity.CanonicalName.ShouldBe("Michael Jackson");
            entity.Kind.ShouldBe(EntityKind.Person);
            entity.Embedding.ShouldNotBeNull();
            entity.StubNoteId.ShouldNotBeNull();

            var suggestion = await db.EntitySuggestions.SingleAsync(s => s.Id == id, ct);
            suggestion.AcceptedAt.ShouldNotBeNull();
            suggestion.AcceptedEntityId.ShouldBe(entityId);

            var stub = await db.Notes.SingleAsync(n => n.Id == entity.StubNoteId!.Value, ct);
            stub.Kind.ShouldBe(NoteKind.EntityStub);
            stub.Status.ShouldBe(NoteStatus.Ready);
            stub.RelativePath.ShouldBe("_Entities/Stubs/Michael Jackson.md");
            stub.BodyOutput!.ShouldContain("- Mike");
        }

        // Idempotent: a second accept returns the same entity, no duplicate.
        var resp2 = await client.PostAsync($"/api/entity-suggestions/{id}/accept", null, ct);
        resp2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted2 = await resp2.Content.ReadFromJsonAsync<AcceptEntitySuggestionResponse>(ct);
        accepted2!.EntityId.ShouldBe(entityId);

        using var probe = NewDb();
        (await probe.Entities.CountAsync(e => e.CanonicalName == "Michael Jackson", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Accept_returns_409_on_path_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        await SeedNoteAtPathAsync("_Entities/Stubs/Michael Jackson.md");
        var id = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2);

        var resp = await client.PostAsync($"/api/entity-suggestions/{id}/accept", null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var conflict = await resp.Content.ReadFromJsonAsync<EntitySuggestionPathConflict>(ct);
        conflict.ShouldNotBeNull();
        conflict!.Conflict.ShouldBe("path");

        using var probe = NewDb();
        (await probe.Entities.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Accept_with_merge_folds_surface_form_into_target_and_creates_no_new_entity()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var targetId = await SeedEntityWithStubAsync("Київ", EntityKind.Place);
        var id = await SeedSuggestionAsync("Kyiv", occurrenceCount: 3, distinctNoteCount: 2,
            kind: EntityKind.Place, suggestedMergeEntityId: targetId);

        var resp = await client.PostAsync(
            $"/api/entity-suggestions/{id}/accept?mergeInto={targetId}", null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await resp.Content.ReadFromJsonAsync<AcceptEntitySuggestionResponse>(ct);
        accepted!.EntityId.ShouldBe(targetId);

        using var probe = NewDb();
        // Folded into the existing node — no second entity minted.
        (await probe.Entities.CountAsync(ct)).ShouldBe(1);
        var target = await probe.Entities.SingleAsync(e => e.Id == targetId, ct);
        target.Aliases.ShouldContain("Kyiv");
        var stub = await probe.Notes.SingleAsync(n => n.Id == target.StubNoteId!.Value, ct);
        stub.BodyOutput!.ShouldContain("- Kyiv");

        var suggestion = await probe.EntitySuggestions.SingleAsync(s => s.Id == id, ct);
        suggestion.AcceptedEntityId.ShouldBe(targetId);
    }

    [Fact]
    public async Task List_surfaces_merge_proposal_with_target_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var targetId = await SeedEntityWithStubAsync("Київ", EntityKind.Place);
        await SeedSuggestionAsync("Kyiv", occurrenceCount: 3, distinctNoteCount: 2,
            kind: EntityKind.Place, suggestedMergeEntityId: targetId, suggestedMergeDistance: 0.33);

        var body = await client.GetFromJsonAsync<ListEntitySuggestionsResponse>("/api/entity-suggestions", ct);
        body!.Suggestions.Count.ShouldBe(1);
        var proposal = body.Suggestions[0].SuggestedMerge;
        proposal.ShouldNotBeNull();
        proposal!.EntityId.ShouldBe(targetId);
        proposal.DisplayName.ShouldBe("Київ");
        proposal.Distance.ShouldBe(0.33, 0.01);
    }

    [Fact]
    public async Task Dismiss_soft_deletes_and_stops_surfacing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var id = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2);

        var resp = await client.PostAsync($"/api/entity-suggestions/{id}/dismiss", null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using (var db = NewDb())
        {
            (await db.EntitySuggestions.SingleAsync(s => s.Id == id, ct)).DismissedAt.ShouldNotBeNull();
        }

        var list = await client.GetFromJsonAsync<ListEntitySuggestionsResponse>("/api/entity-suggestions", ct);
        list!.Suggestions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_renames_canonical_before_accept()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var id = await SeedSuggestionAsync("Mj", occurrenceCount: 3, distinctNoteCount: 2);

        var patch = new EditEntitySuggestionRequest("Michael Jackson", ["Mike", "MJ"]);
        var resp = await client.PostAsJsonAsync($"/api/entity-suggestions/{id}/edit", patch, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var probe = NewDb();
        var s = await probe.EntitySuggestions.SingleAsync(x => x.Id == id, ct);
        s.CanonicalText.ShouldBe("Michael Jackson");
        s.Aliases.ShouldBe(["Mike", "MJ"]);
    }

    [Fact]
    public async Task Edit_after_accept_adds_alias_to_entity_and_stub()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var id = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2,
            aliases: ["Mike"]);
        var accept = await client.PostAsync($"/api/entity-suggestions/{id}/accept", null, ct);
        var entityId = (await accept.Content.ReadFromJsonAsync<AcceptEntitySuggestionResponse>(ct))!.EntityId;

        var patch = new EditEntitySuggestionRequest(null, ["Mike", "MJ", "Jackson"]);
        var resp = await client.PostAsJsonAsync($"/api/entity-suggestions/{id}/edit", patch, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var probe = NewDb();
        var entity = await probe.Entities.SingleAsync(e => e.Id == entityId, ct);
        entity.Aliases.ShouldContain("Jackson");
        var stub = await probe.Notes.SingleAsync(n => n.Id == entity.StubNoteId!.Value, ct);
        stub.BodyOutput!.ShouldContain("- Jackson");
    }

    [Fact]
    public async Task Edit_rename_after_accept_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (factory, client) = await BuildAuthedClientAsync();
        await using var f = factory;

        var id = await SeedSuggestionAsync("Michael Jackson", occurrenceCount: 3, distinctNoteCount: 2);
        await client.PostAsync($"/api/entity-suggestions/{id}/accept", null, ct);

        var patch = new EditEntitySuggestionRequest("Michael J", null);
        var resp = await client.PostAsJsonAsync($"/api/entity-suggestions/{id}/edit", patch, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_without_bearer_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/entity-suggestions", ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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

    private async Task<Guid> SeedSuggestionAsync(
        string canonical, int occurrenceCount, int distinctNoteCount, string[]? aliases = null,
        string kind = EntityKind.Person, Guid? suggestedMergeEntityId = null,
        double? suggestedMergeDistance = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var occurrences = Enumerable.Range(0, Math.Max(occurrenceCount, 1))
            .Select(i => new EntitySuggestionOccurrence(
                NoteId: Guid.CreateVersion7(),
                AnchorText: canonical,
                SurroundingText: $"context {i}",
                ObservedAt: now.ToDateTimeOffset()))
            .ToList();

        using var db = NewDb();
        var s = new EntitySuggestion
        {
            Id = Guid.CreateVersion7(),
            CanonicalText = canonical,
            Kind = kind,
            Aliases = aliases ?? [],
            Occurrences = EntitySuggestionOccurrences.Serialize(occurrences),
            OccurrenceCount = occurrenceCount,
            DistinctNoteCount = distinctNoteCount,
            Embedding = new Vector(FakeEmbeddingClient.DeterministicUnitVector(canonical)),
            SuggestedMergeEntityId = suggestedMergeEntityId,
            SuggestedMergeDistance = suggestedMergeDistance,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
        };
        db.EntitySuggestions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private async Task<Guid> SeedEntityWithStubAsync(string canonical, string kind)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        var entityId = Guid.CreateVersion7();
        var stub = new Note
        {
            Id = Guid.CreateVersion7(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            Kind = NoteKind.EntityStub,
            BodyInput = string.Empty,
            BodyOutput = EntityStubWriter.BuildStubMarkdown(canonical, kind, [], entityId),
            RelativePath = $"_Entities/Stubs/{canonical}.md",
            Tags = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(stub);
        db.Entities.Add(new Entity
        {
            Id = entityId,
            Kind = kind,
            CanonicalName = canonical,
            Source = EntitySource.User,
            Embedding = new Vector(FakeEmbeddingClient.DeterministicUnitVector(canonical)),
            StubNoteId = stub.Id,
            MentionCount = 5,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return entityId;
    }

    private async Task SeedNoteAtPathAsync(string relativePath)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        db.Notes.Add(new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "x",
            BodyOutput = "x",
            RelativePath = relativePath,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedPluginTokenAsync()
    {
        var raw = "es-test-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        using var db = NewDb();
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 30, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
