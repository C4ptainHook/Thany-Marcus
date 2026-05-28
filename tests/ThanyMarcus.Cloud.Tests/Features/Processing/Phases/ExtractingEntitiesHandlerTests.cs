using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class ExtractingEntitiesHandlerTests(PostgresFixture postgres)
{
    private static readonly string[] EmptyAliases = Array.Empty<string>();

    [Fact]
    public async Task Extract_then_new_entity_dedup_inserts_entity_and_mention()
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
            DedupResponse = () => new DedupDecisionDto(
                DedupDecisions.NewEntity, null, Array.Empty<Guid>(), 0.9, "not present"),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);

        var entities = await probe.Entities.ToListAsync(ct);
        entities.Count.ShouldBe(1);
        entities[0].CanonicalName.ShouldBe("John Smith");
        entities[0].Source.ShouldBe(EntitySource.Llm);
        entities[0].IsProvisional.ShouldBeTrue();
        entities[0].MentionCount.ShouldBe(1);

        var mentions = await probe.Mentions.Where(m => m.NoteId == noteId).ToListAsync(ct);
        mentions.Count.ShouldBe(1);
        mentions[0].EntityId.ShouldBe(entities[0].Id);
        mentions[0].StartOffset.ShouldBe(0);
        mentions[0].EndOffset.ShouldBe(10);
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
        llm.Calls.ShouldNotContain(c => c.Name == "dedup");
    }

    [Fact]
    public async Task Mention_count_crossing_threshold_spawns_hub_note_and_regen_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var existingEntityId = Guid.CreateVersion7();
        await SeedExistingEntityAsync(existingEntityId, "Acme Corp", mentionCount: 2);

        var (noteId, jobId) = await SeedJobAsync(IngestJobStatus.ExtractingEntities,
            bodyOutput: "Acme Corp won the contract.");

        var llm = new ConfigurableLlmClient
        {
            ExtractResponse = () => new EntityExtractionDto(new[]
            {
                new MentionCandidateDto("Acme Corp", 0, 9, EntityKind.Organization, "Acme Corp",
                    EmptyAliases, 0.95),
            }),
            DedupResponse = () => new DedupDecisionDto(
                DedupDecisions.AliasOf, existingEntityId, new[] { existingEntityId }, 0.9, "same"),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var entity = await probe.Entities.SingleAsync(e => e.Id == existingEntityId, ct);
        entity.MentionCount.ShouldBe(3);
        entity.HubNoteId.ShouldNotBeNull();

        var hubNote = await probe.Notes.SingleAsync(n => n.Id == entity.HubNoteId!.Value, ct);
        hubNote.IsHub.ShouldBeTrue();
        hubNote.HubEntityId.ShouldBe(existingEntityId);
        hubNote.RelativePath.ShouldStartWith("_Entities/");

        var hubJob = await probe.IngestJobs.SingleAsync(j => j.NoteId == hubNote.Id, ct);
        hubJob.Kind.ShouldBe(IngestJobKind.HubRegen);
        hubJob.Status.ShouldBe(IngestJobStatus.Composing);
    }

    [Fact]
    public async Task Deleted_note_breaks_loop_without_inserting_mentions()
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
        llm.Calls.ShouldBeEmpty();
    }

    private ServiceProvider BuildHost(ConfigurableLlmClientFactory factory) =>
        ProcessingTestHost.Build(postgres.ConnectionString, customize: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var t = services[i].ServiceType;
                if (t == typeof(ILlmClient) || t == typeof(ILlmClientFactory))
                    services.RemoveAt(i);
            }
            services.AddSingleton<ILlmClient>(factory.Client);
            services.AddSingleton<ILlmClientFactory>(factory);
        });

    private async Task SeedExistingEntityAsync(Guid id, string canonical, int mentionCount)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        db.Entities.Add(new Entity
        {
            Id            = id,
            Kind          = EntityKind.Organization,
            CanonicalName = canonical,
            Source        = EntitySource.Llm,
            IsProvisional = true,
            MentionCount  = mentionCount,
            Embedding     = new Vector(new float[256]),
            CreatedAt     = now,
            UpdatedAt     = now,
        });
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
