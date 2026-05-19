using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class HubGenerationHandlerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Initial_regen_writes_body_output_with_frontmatter_and_sections()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (entityId, hubNoteId, jobId) = await SeedHubJobAsync(previousBody: null);

        var llm = new ConfigurableLlmClient
        {
            HubGenerateResponse = () => "### Context\n\n- a knowledge fact",
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var hubNote = await probe.Notes.SingleAsync(n => n.Id == hubNoteId, ct);
        hubNote.BodyOutput.ShouldNotBeNullOrEmpty();
        hubNote.BodyOutput!.ShouldStartWith("---\n");
        hubNote.BodyOutput.ShouldContain("## User Notes");
        hubNote.BodyOutput.ShouldContain("## System Output");
        hubNote.BodyOutput.ShouldContain("### Context");
        hubNote.BodyOutput.ShouldContain("a knowledge fact");

        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);

        llm.Calls.ShouldContain(c => c.Name == "hub-generate");
    }

    [Fact]
    public async Task Diff_aware_regen_preserves_user_notes_block()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        const string previousBody = "---\nid: x\n---\n\n## User Notes\n\nmy hand-written stuff\n\n## System Output\n\n### Context\n\n- old\n";
        var (entityId, hubNoteId, jobId) = await SeedHubJobAsync(previousBody);

        var llm = new ConfigurableLlmClient
        {
            HubGenerateResponse = () => "### Context\n\n- updated fact",
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var hubNote = await probe.Notes.SingleAsync(n => n.Id == hubNoteId, ct);
        hubNote.BodyOutput.ShouldNotBeNull();
        hubNote.BodyOutput!.ShouldContain("my hand-written stuff");
        hubNote.BodyOutput.ShouldContain("updated fact");
    }

    [Fact]
    public async Task Missing_hub_entity_id_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        Guid hubNoteId;
        using (var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            var hubNote = new Note
            {
                Id           = Guid.CreateVersion7(),
                CapturedAt   = now,
                BodyInput    = "",
                IsHub        = true,
                HubEntityId  = null,
                RelativePath = "_Entities/x/Y.md",
                Status       = NoteStatus.Pending,
                CreatedAt    = now,
                UpdatedAt    = now,
            };
            hubNoteId = hubNote.Id;
            db.Notes.Add(hubNote);
            await db.SaveChangesAsync(ct);
        }

        var llm = new ConfigurableLlmClient();
        var factory = new ConfigurableLlmClientFactory(llm);
        await using var sp = BuildHost(factory);

        await using var scope = sp.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HubGenerationHandler>();
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = hubNoteId,
            Kind = IngestJobKind.HubRegen,
            Status = IngestJobStatus.Composing,
            ScheduledAt = now,
        };
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await handler.HandleAsync(job, ct));
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

    private async Task<(Guid entityId, Guid hubNoteId, Guid jobId)> SeedHubJobAsync(string? previousBody)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var entity = new Entity
        {
            Id            = Guid.CreateVersion7(),
            Kind          = EntityKind.Person,
            CanonicalName = "John Smith",
            Source        = EntitySource.Llm,
            IsProvisional = true,
            MentionCount  = 3,
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        var hubNote = new Note
        {
            Id           = Guid.CreateVersion7(),
            CapturedAt   = now,
            BodyInput    = "",
            BodyOutput   = previousBody,
            IsHub        = true,
            HubEntityId  = entity.Id,
            RelativePath = "_Entities/person/John Smith.md",
            Status       = NoteStatus.Pending,
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        entity.HubNoteId = hubNote.Id;
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = hubNote.Id,
            Kind           = IngestJobKind.HubRegen,
            Status         = IngestJobStatus.Composing,
            Attempts       = 1,
            LeaseOwner     = "test-worker/abc12345",
            LeaseExpiresAt = now.Plus(Duration.FromSeconds(60)),
            ScheduledAt    = now,
            StartedAt      = now,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.Entities.Add(entity);
        db.Notes.Add(hubNote);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (entity.Id, hubNote.Id, job.Id);
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
