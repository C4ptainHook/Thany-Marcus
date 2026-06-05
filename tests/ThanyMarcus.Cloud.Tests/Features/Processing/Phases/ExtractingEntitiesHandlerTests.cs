using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class ExtractingEntitiesHandlerTests(PostgresFixture postgres)
{
    private static readonly string[] EmptyAliases = Array.Empty<string>();
    private static readonly string[] ExpectedRealCanonicals = { "Sarah Chen", "Project Atlas", "OpenAI", "Berlin" };
    private static readonly string[] BlobAliases =
    {
        "She wants the OpenAI integration shipped before the Berlin offsite.",
        "OpenAI's embeddings API",
    };

    [Fact]
    public async Task Unknown_candidate_creates_a_suggestion_not_an_entity()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "John Smith joined Acme today.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("John Smith", 0, 10, EntityKind.Person, "John Smith",
                    EmptyAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);

        // No auto-created entity, no mention — the candidate accumulates as a suggestion instead.
        (await probe.Entities.CountAsync(ct)).ShouldBe(0);
        (await probe.Mentions.CountAsync(m => m.NoteId == noteId, ct)).ShouldBe(0);

        var suggestions = await probe.EntitySuggestions.ToListAsync(ct);
        suggestions.Count.ShouldBe(1);
        suggestions[0].CanonicalText.ShouldBe("John Smith");
        suggestions[0].Kind.ShouldBe(EntityKind.Person);
        suggestions[0].OccurrenceCount.ShouldBe(1);
        suggestions[0].DistinctNoteCount.ShouldBe(1);
        suggestions[0].AcceptedAt.ShouldBeNull();
        suggestions[0].DismissedAt.ShouldBeNull();

        llm.Calls.ShouldNotContain(c => c.Name == "dedup");
    }

    [Fact]
    public async Task Below_mention_min_candidate_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "vague mention");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("vague", 0, 5, EntityKind.Other, "vague",
                    EmptyAliases, 0.3),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        (await probe.Entities.CountAsync(ct)).ShouldBe(0);
        (await probe.Mentions.CountAsync(m => m.NoteId == noteId, ct)).ShouldBe(0);
        (await probe.EntitySuggestions.CountAsync(ct)).ShouldBe(0);
        llm.Calls.ShouldNotContain(c => c.Name == "dedup");
    }

    [Fact]
    public async Task Garbage_canonical_is_dropped_by_the_name_guard()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "Notes on zero stock picking and index funds.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("zero stock picking", 9, 27, EntityKind.Concept,
                    "zero stock picking", EmptyAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        (await probe.Entities.CountAsync(ct)).ShouldBe(0);
        (await probe.Mentions.CountAsync(m => m.NoteId == noteId, ct)).ShouldBe(0);
        (await probe.EntitySuggestions.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Real_entities_surface_separately_while_the_blob_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "Sarah Chen leads Project Atlas; OpenAI ships before the Berlin offsite.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("Sarah Chen", 0, 10, EntityKind.Person, "Sarah Chen", EmptyAliases, 0.95),
                new MentionCandidateDto("Project Atlas", 17, 30, EntityKind.Organization, "Project Atlas", EmptyAliases, 0.95),
                new MentionCandidateDto("OpenAI", 32, 38, EntityKind.Organization, "OpenAI", EmptyAliases, 0.95),
                new MentionCandidateDto("Berlin", 53, 59, EntityKind.Place, "Berlin", EmptyAliases, 0.95),
                new MentionCandidateDto("stock picking", 0, 5, EntityKind.Concept, "zero stock picking",
                    BlobAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory, RouteByCanonical);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var rows = await probe.EntitySuggestions.ToListAsync(ct);
        rows.Select(r => r.CanonicalText).ShouldBe(ExpectedRealCanonicals, ignoreOrder: true);
        rows.ShouldNotContain(r => r.CanonicalText == "zero stock picking");
        rows.SelectMany(r => r.Aliases).ShouldNotContain(a => a.Contains(' ') && a.EndsWith('.'));
    }

    private static float[] RouteByCanonical(string text) =>
        text.StartsWith("Sarah", StringComparison.Ordinal) ? Axis(0)
        : text.StartsWith("Project Atlas", StringComparison.Ordinal) ? Axis(1)
        : text.StartsWith("OpenAI", StringComparison.Ordinal) ? Axis(2)
        : text.StartsWith("Berlin", StringComparison.Ordinal) ? Axis(3)
        : Axis(4);

    [Fact]
    public async Task KnnMatch_against_curated_entity_creates_mention_and_spawns_hub()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var existingEntityId = Guid.CreateVersion7();
        await SeedExistingEntityAsync(existingEntityId, "Acme Corp", mentionCount: 2, embedding: Axis(0));

        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "Acme Corp won the contract.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("Acme Corp", 0, 9, EntityKind.Organization, "Acme Corp",
                    EmptyAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        // Candidate embeds onto the same vector as the entity → distance 0 → tight auto-merge.
        await using var sp = BuildHost(factory, _ => Axis(0));
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var entity = await probe.Entities.SingleAsync(e => e.Id == existingEntityId, ct);
        entity.MentionCount.ShouldBe(3);
        entity.HubNoteId.ShouldNotBeNull();

        var mentions = await probe.Mentions.Where(m => m.NoteId == noteId).ToListAsync(ct);
        mentions.Count.ShouldBe(1);
        mentions[0].EntityId.ShouldBe(existingEntityId);

        // A confident kNN match never falls through to a suggestion.
        (await probe.EntitySuggestions.CountAsync(ct)).ShouldBe(0);

        var hubNote = await probe.Notes.SingleAsync(n => n.Id == entity.HubNoteId!.Value, ct);
        hubNote.IsHub.ShouldBeTrue();
        hubNote.HubEntityId.ShouldBe(existingEntityId);

        var hubJob = await probe.IngestJobs.SingleAsync(j => j.NoteId == hubNote.Id, ct);
        hubJob.Kind.ShouldBe(IngestJobKind.HubRegen);
        hubJob.Status.ShouldBe(IngestJobStatus.Composing);
    }

    [Fact]
    public async Task GrayZone_match_routes_to_suggestion_with_merge_proposal()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var kyivId = Guid.CreateVersion7();
        await SeedExistingEntityAsync(kyivId, "Київ", mentionCount: 5, embedding: Axis(0),
            kind: EntityKind.Place);

        var (_, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "We flew into Kyiv last spring.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("Kyiv", 13, 17, EntityKind.Place, "Kyiv", EmptyAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory, _ => AtDistance(0.15));
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        // No auto-merge: the entity gets no new mention and no new alias.
        var kyiv = await probe.Entities.SingleAsync(e => e.Id == kyivId, ct);
        kyiv.MentionCount.ShouldBe(5);
        kyiv.Aliases.ShouldNotContain("Kyiv");

        var suggestion = await probe.EntitySuggestions.SingleAsync(ct);
        suggestion.CanonicalText.ShouldBe("Kyiv");
        suggestion.SuggestedMergeEntityId.ShouldBe(kyivId);
        suggestion.SuggestedMergeDistance!.Value.ShouldBe(0.15, 0.01);
    }

    [Fact]
    public async Task TightMatch_appends_alias_and_syncs_stub_frontmatter()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        // Curated entity that already has a materialized stub — the new surface form must reach it.
        var kyivId = Guid.CreateVersion7();
        await SeedExistingEntityAsync(kyivId, "Київ", mentionCount: 1, embedding: Axis(0),
            kind: EntityKind.Place, withStub: true);

        var (_, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "Знову до Києва.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("Києва", 8, 13, EntityKind.Place, "Київ", EmptyAliases, 0.95),
            }),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory, _ => Axis(0));
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var kyiv = await probe.Entities.SingleAsync(e => e.Id == kyivId, ct);
        kyiv.Aliases.ShouldContain("Києва");

        var stub = await probe.Notes.SingleAsync(n => n.Id == kyiv.StubNoteId!.Value, ct);
        stub.BodyOutput!.ShouldContain("- Києва");
    }

    [Fact]
    public async Task Deleted_note_breaks_loop_without_inserting_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "body", deletedAt: now);

        var llm = new ConfigurableLlmClient();
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        (await probe.Mentions.CountAsync(m => m.NoteId == noteId, ct)).ShouldBe(0);
        (await probe.EntitySuggestions.CountAsync(ct)).ShouldBe(0);
        llm.Calls.ShouldBeEmpty();
    }

    private ServiceProvider BuildHost(ConfigurableLlmClientFactory factory, Func<string, float[]>? embed = null) =>
        ProcessingTestHost.Build(postgres.ConnectionString, customize: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var t = services[i].ServiceType;
                if (t == typeof(ILlmClient) || t == typeof(ILlmClientFactory))
                    services.RemoveAt(i);
                else if (embed is not null && t == typeof(IEmbeddingClient))
                    services.RemoveAt(i);
            }
            services.AddSingleton<ILlmClient>(factory.Client);
            services.AddSingleton<ILlmClientFactory>(factory);
            if (embed is not null)
                services.AddSingleton<IEmbeddingClient>(new FakeEmbeddingClient(embed));
        });

    // Unit vector along axis i — a valid, normalised embedding for controlling kNN distance.
    private static float[] Axis(int i)
    {
        var v = new float[FakeEmbeddingClient.Dimensions];
        v[i] = 1f;
        return v;
    }

    // Unit vector at exactly cosine distance d from Axis(0).
    private static float[] AtDistance(double d)
    {
        var v = new float[FakeEmbeddingClient.Dimensions];
        var a = 1.0 - d;
        v[0] = (float)a;
        v[1] = (float)Math.Sqrt(Math.Max(0, 1 - a * a));
        return v;
    }

    private async Task SeedExistingEntityAsync(
        Guid id, string canonical, int mentionCount, float[] embedding,
        string kind = EntityKind.Organization, bool withStub = false)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var entity = new Entity
        {
            Id            = id,
            Kind          = kind,
            CanonicalName = canonical,
            Source        = EntitySource.User,
            MentionCount  = mentionCount,
            Embedding     = new Vector(embedding),
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        db.Entities.Add(entity);
        if (withStub)
        {
            var stub = new Note
            {
                Id           = Guid.CreateVersion7(),
                CapturedAt   = now,
                Status       = NoteStatus.Ready,
                Kind         = NoteKind.EntityStub,
                BodyInput    = string.Empty,
                BodyOutput   = EntityStubWriter.BuildStubMarkdown(canonical, kind, entity.Aliases, id),
                RelativePath = $"_Entities/Stubs/{canonical}.md",
                Tags         = [],
                CreatedAt    = now,
                UpdatedAt    = now,
            };
            db.Notes.Add(stub);
            entity.StubNoteId = stub.Id;
        }
        await db.SaveChangesAsync();
    }

    private async Task<(Guid noteId, Guid jobId)> SeedJobAsync(
        string phaseStatus, string bodyOutput, Instant? deletedAt = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var noteId = Guid.CreateVersion7();
        var note = new Note
        {
            Id           = noteId,
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Processing,
            BodyInput    = bodyOutput,
            BodyOutput   = bodyOutput,
            RelativePath = $"Inbox/{noteId}.md",
            DeletedAt    = deletedAt,
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Kind           = IngestJobKind.Capture,
            Status         = phaseStatus,
            Attempts       = 1,
            LeaseOwner     = "test-worker/abc12345",
            LeaseExpiresAt = now.Plus(Duration.FromSeconds(60)),
            ScheduledAt    = now,
            StartedAt      = now,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (note.Id, job.Id);
    }

    private async Task<IngestJob> LoadJobAsync(Guid jobId)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        return await db.IngestJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private static async Task DispatchAsync(ServiceProvider sp, IngestJob job, CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
        await dispatcher.DispatchAsync(job, ct);
    }
}
