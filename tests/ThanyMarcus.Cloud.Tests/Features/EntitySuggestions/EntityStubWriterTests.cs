using Microsoft.EntityFrameworkCore;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.EntitySuggestions;

[Collection(PostgresCollection.Name)]
public sealed class EntityStubWriterTests(PostgresFixture postgres)
{
    private static readonly StaticOptionsMonitor<LlmIntelligenceOptions> DefaultOpts = new(new LlmIntelligenceOptions());
    private static readonly string[] MjAliases = ["Mike", "MJ", "Jackson"];
    private static readonly string[] MikeAlias = ["Mike"];
    private static readonly string[] NoAliases = [];

    [Fact]
    public void BuildStubMarkdown_is_deterministic()
    {
        var id = Guid.Parse("019e7d3a-0000-7000-8000-000000000001");
        var md = EntityStubWriter.BuildStubMarkdown("Michael Jackson", "person", MjAliases, id);
        md.ShouldBe(
            "---\n" +
            "aliases:\n" +
            "  - Mike\n" +
            "  - MJ\n" +
            "  - Jackson\n" +
            $"thany:entity_id: {id}\n" +
            "thany:kind: person\n" +
            "---\n\n");
    }

    [Fact]
    public void BuildStubMarkdown_empty_aliases_uses_inline_list()
    {
        var id = Guid.CreateVersion7();
        var md = EntityStubWriter.BuildStubMarkdown("Solo", "concept", NoAliases, id);
        md.ShouldContain("aliases: []");
        md.ShouldNotContain("  - ");
    }

    [Fact]
    public void ComputeStubRelativePath_sanitises_and_prefixes_folder()
    {
        using var db = NewDb();
        var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
        w.ComputeStubRelativePath("Michael Jackson").ShouldBe("_Entities/Stubs/Michael Jackson.md");
        w.ComputeStubRelativePath("A/B:C*?\"<>|").ShouldBe("_Entities/Stubs/A B C.md");
    }

    [Fact]
    public void ComputeStubRelativePath_honours_configured_folder()
    {
        using var db = NewDb();
        var opts = new StaticOptionsMonitor<LlmIntelligenceOptions>(new LlmIntelligenceOptions
        {
            EntitySuggestions = new EntitySuggestionsOptions { StubsFolder = "People" },
        });
        var w = new EntityStubWriter(db, opts, SystemClock.Instance);
        w.ComputeStubRelativePath("Jane Doe").ShouldBe("People/Jane Doe.md");
    }

    [Fact]
    public async Task CreateAsync_inserts_entity_stub_note_linked_via_stub_note_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var entityId = await SeedEntityAsync("Michael Jackson", "person", MikeAlias);

        using (var db = NewDb())
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
            var note = await w.CreateAsync(entity, entity.Aliases, ct);
            await db.SaveChangesAsync(ct);
            note.Kind.ShouldBe(NoteKind.EntityStub);
            note.Status.ShouldBe(NoteStatus.Ready);
            note.RelativePath.ShouldBe("_Entities/Stubs/Michael Jackson.md");
            note.BodyOutput!.ShouldContain("- Mike");
        }

        using var probe = NewDb();
        var stored = await probe.Entities.SingleAsync(e => e.Id == entityId, ct);
        stored.StubNoteId.ShouldNotBeNull();
        var stub = await probe.Notes.SingleAsync(n => n.Id == stored.StubNoteId!.Value, ct);
        stub.Kind.ShouldBe(NoteKind.EntityStub);
        stub.Status.ShouldBe(NoteStatus.Ready);
    }

    [Fact]
    public async Task UpdateAliasesAsync_rewrites_aliases_and_preserves_user_body()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var entityId = await SeedEntityAsync("Michael Jackson", "person", MikeAlias);

        // Create the stub, then simulate the user adding their own body content below the fence.
        using (var db = NewDb())
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
            var note = await w.CreateAsync(entity, entity.Aliases, ct);
            note.BodyOutput += "My personal dossier notes.\n";
            await db.SaveChangesAsync(ct);
        }

        using (var db = NewDb())
        {
            var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
            entity.Aliases = MjAliases;
            var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
            await w.UpdateAliasesAsync(entity, entity.Aliases, ct);
            await db.SaveChangesAsync(ct);
        }

        using var probe = NewDb();
        var stored = await probe.Entities.SingleAsync(e => e.Id == entityId, ct);
        var stub = await probe.Notes.SingleAsync(n => n.Id == stored.StubNoteId!.Value, ct);
        stub.BodyOutput!.ShouldContain("- MJ");
        stub.BodyOutput!.ShouldContain("- Jackson");
        stub.BodyOutput!.ShouldContain("My personal dossier notes.");
    }

    [Fact]
    public async Task CreateAsync_throws_on_path_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SeedNoteAtPathAsync("_Entities/Stubs/Michael Jackson.md");
        var entityId = await SeedEntityAsync("Michael Jackson", "person", NoAliases);

        using var db = NewDb();
        var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
        var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
        await Should.ThrowAsync<StubPathConflictException>(async () =>
            await w.CreateAsync(entity, entity.Aliases, ct));
    }

    [Fact]
    public void ExtractBodyAfterFrontmatter_returns_body_after_closing_fence()
    {
        const string content = "---\naliases: []\nthany:kind: person\n---\n\nbody text\n";
        EntityStubWriter.ExtractBodyAfterFrontmatter(content).ShouldBe("\nbody text\n");
    }

    [Fact]
    public async Task IsAliasClaimedElsewhere_detects_other_entity_and_literal_note_owners()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        // Another live entity owns "Kyiv" as an alias; a literal user note owns the basename "Foo".
        var e1 = await SeedEntityAsync("Київ", "place", NoAliases);
        await SeedEntityAsync("Kyiv City", "place", ["Kyiv"]);
        await SeedNoteAtPathAsync("Places/Foo.md");

        using var db = NewDb();
        var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);

        (await w.IsAliasClaimedElsewhereAsync(e1, "Kyiv", ct)).ShouldBeTrue();
        (await w.IsAliasClaimedElsewhereAsync(e1, "Foo", ct)).ShouldBeTrue();
        (await w.IsAliasClaimedElsewhereAsync(e1, "Kyyiv", ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsAliasClaimedElsewhere_excludes_the_entitys_own_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var e = await SeedEntityAsync("Kyiv City", "place", ["Kyiv"]);

        using var db = NewDb();
        var w = new EntityStubWriter(db, DefaultOpts, SystemClock.Instance);
        (await w.IsAliasClaimedElsewhereAsync(e, "Kyiv", ct)).ShouldBeFalse();
        (await w.IsAliasClaimedElsewhereAsync(e, "Kyiv City", ct)).ShouldBeFalse();
    }

    private async Task<Guid> SeedEntityAsync(string canonical, string kind, string[] aliases)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb();
        var e = new Entity
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            CanonicalName = canonical,
            Aliases = aliases,
            Source = EntitySource.User,
            Embedding = new Vector(new float[256]),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Entities.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
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

    private CloudDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
