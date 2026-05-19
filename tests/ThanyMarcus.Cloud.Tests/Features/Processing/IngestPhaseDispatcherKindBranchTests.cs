using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

[Collection(PostgresCollection.Name)]
public sealed class IngestPhaseDispatcherKindBranchTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Composing_capture_routes_to_composing_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var llm = new ConfigurableLlmClient();
        var factory = new ConfigurableLlmClientFactory(llm);
        await using var sp = BuildHost(factory);

        var (_, jobId) = await SeedCaptureComposingAsync();
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);
        // capture-composing does not invoke hub-generate
        llm.Calls.ShouldNotContain(c => c.Name == "hub-generate");
    }

    [Fact]
    public async Task Composing_hub_regen_routes_to_hub_generation_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var llm = new ConfigurableLlmClient
        {
            HubGenerateResponse = () => "### Context\n\n- body",
        };
        var factory = new ConfigurableLlmClientFactory(llm);
        await using var sp = BuildHost(factory);

        var (_, jobId) = await SeedHubRegenComposingAsync();
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);
        llm.Calls.ShouldContain(c => c.Name == "hub-generate");
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

    private async Task<(Guid noteId, Guid jobId)> SeedCaptureComposingAsync()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id           = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Processing,
            BodyInput    = "capture body",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Kind           = IngestJobKind.Capture,
            Status         = IngestJobStatus.Composing,
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

    private async Task<(Guid noteId, Guid jobId)> SeedHubRegenComposingAsync()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var entity = new Entity
        {
            Id            = Guid.CreateVersion7(),
            Kind          = EntityKind.Person,
            CanonicalName = "X",
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
            Status       = NoteStatus.Pending,
            BodyInput    = "",
            IsHub        = true,
            HubEntityId  = entity.Id,
            RelativePath = "_Entities/person/X.md",
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
        return (hubNote.Id, job.Id);
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
