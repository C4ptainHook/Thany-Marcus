using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class RoutingHandlerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task High_confidence_route_writes_project_id_and_projects_path()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var projectId = Guid.CreateVersion7();
        await SeedProjectAsync(projectId, "Acme");

        var (noteId, jobId) = await SeedRoutingJobAsync();

        var llm = new ConfigurableLlmClient
        {
            RouteResponse = () => new RouteDecisionDto(projectId, 0.9, "matches Acme"),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.ProjectId.ShouldBe(projectId);
        note.RelativePath.ShouldBe($"Acme/{noteId}.md");

        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Synthesizing);
        llm.Calls.ShouldContain(c => c.Name == "route" && c.Version == "v1");
    }

    [Fact]
    public async Task Below_threshold_confidence_keeps_inbox_path()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var projectId = Guid.CreateVersion7();
        await SeedProjectAsync(projectId, "Acme");

        var (noteId, jobId) = await SeedRoutingJobAsync();
        var llm = new ConfigurableLlmClient
        {
            RouteResponse = () => new RouteDecisionDto(projectId, 0.3, "weak"),
        };
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.ProjectId.ShouldBeNull();
        note.RelativePath.ShouldBe($"Inbox/{noteId}.md");
    }

    [Fact]
    public async Task Hub_note_short_circuits_to_embedding_without_llm_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (noteId, jobId) = await SeedRoutingJobAsync(isHub: true);
        var llm = new ConfigurableLlmClient();
        var factory = new ConfigurableLlmClientFactory(llm);

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Embedding);
        llm.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fallback_to_safe_records_llm_mode_fallback_in_events_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await SeedProjectAsync(Guid.CreateVersion7(), "Acme");
        var (_, jobId) = await SeedRoutingJobAsync();
        var llm = new ConfigurableLlmClient
        {
            RouteResponse = () => new RouteDecisionDto(null, 0.2, "no match"),
        };
        var factory = new ConfigurableLlmClientFactory(llm) { FallbackToSafe = true };

        await using var sp = BuildHost(factory);
        var job = await LoadJobAsync(jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        var events = after.EventsLog.RootElement.EnumerateArray().ToList();
        var routeEventHasFallback = events.Any(e =>
        {
            if (!e.TryGetProperty("stage", out var s) || s.GetString() != "llm_route") return false;
            return e.TryGetProperty("llm_mode_fallback", out var f) && f.GetBoolean();
        });
        routeEventHasFallback.ShouldBeTrue();
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

    private async Task SeedProjectAsync(Guid projectId, string canonical)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var now = SystemClock.Instance.GetCurrentInstant();
        db.Entities.Add(new Entity
        {
            Id            = projectId,
            Kind          = EntityKind.Project,
            CanonicalName = canonical,
            Source        = EntitySource.User,
            CreatedAt     = now,
            UpdatedAt     = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid noteId, Guid jobId)> SeedRoutingJobAsync(bool isHub = false)
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
            BodyInput    = "test body",
            BodyOutput   = "# composed body about Acme Corp",
            RelativePath = $"Inbox/{noteId}.md",
            IsHub        = isHub,
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Kind           = IngestJobKind.Capture,
            Status         = IngestJobStatus.Routing,
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
