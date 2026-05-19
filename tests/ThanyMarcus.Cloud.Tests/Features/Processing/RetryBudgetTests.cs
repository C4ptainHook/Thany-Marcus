using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

[Collection(PostgresCollection.Name)]
public sealed class RetryBudgetTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Composing_handler_throw_reschedules_and_increments_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var sp = BuildHostWithThrowingComposer(postgres.ConnectionString, throwCount: 1);
        var (_, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Composing, attempts: 1);

        var job = await LoadJobAsync(postgres, jobId);
        var beforeDispatchNow = SystemClock.Instance.GetCurrentInstant();
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Composing);
        after.LastError.ShouldNotBeNull();
        after.LeaseOwner.ShouldBeNull();
        after.ScheduledAt.ShouldBeGreaterThan(beforeDispatchNow);
    }

    [Fact]
    public async Task Composing_handler_exhausts_budget_then_lands_in_failed_composition()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var sp = BuildHostWithThrowingComposer(postgres.ConnectionString, throwCount: 99);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Composing, attempts: 2);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.FailedComposition);
        after.FinishedAt.ShouldNotBeNull();
        after.LeaseOwner.ShouldBeNull();

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.Status.ShouldBe(NoteStatus.Failed);
    }

    private static async Task<(Guid noteId, Guid jobId)> SeedClaimedJobAsync(
        PostgresFixture postgres, string status, short attempts)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id           = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Processing,
            BodyInput    = "test body",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Status         = status,
            Attempts       = attempts,
            LeaseOwner     = "test-worker/feedbeef",
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

    private static async Task<IngestJob> LoadJobAsync(PostgresFixture postgres, Guid jobId)
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

    private static ServiceProvider BuildHostWithThrowingComposer(string connectionString, int throwCount)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<TimestampInterceptor>();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnv());
        services.AddSingleton<IConfiguration>(_ =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IngestSaga:Phases:composing:MaxAttempts"] = "2",
                ["IngestSaga:Phases:composing:BackoffSecondsBase"] = "1",
                ["IngestSaga:Models:Stub:Version"] = "stub-v1",
            }).Build());

        services.AddDbContext<CloudDbContext>((sp, opts) => opts
            .UseNpgsql(connectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        services.Configure<LlmIntelligenceOptions>(_ => { });
        services.AddSingleton<ILlmClient, StubLlmClient>();
        services.AddSingleton<ILlmClientFactory, StubLlmClientFactory>();
        services.AddScoped<LlmEventAppender>();
        services.AddSingleton<IEmbeddingClient>(_ =>
            new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector));
        services.AddSingleton<IIngestEventBus, NoOpIngestEventBus>();
        services.AddSingleton(new ThrowCounter { Remaining = throwCount });

        services.AddScoped<JobStateTransitions>();
        services.AddScoped<ProvenanceMaterializer>();
        services.AddScoped<IPhaseHandler, ThrowingComposingHandler>();
        services.AddScoped<CancelHandler>();
        services.AddScoped<HubGenerationHandler>();
        services.AddScoped<IngestPhaseDispatcher>();
        return services.BuildServiceProvider();
    }

    private sealed class ThrowCounter
    {
        public int Remaining { get; set; }
    }

    private sealed class ThrowingComposingHandler : IPhaseHandler
    {
        private readonly ThrowCounter counter;
        public ThrowingComposingHandler(ThrowCounter counter) { this.counter = counter; }
        public string Phase => IngestJobStatus.Composing;
        public Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
        {
            if (counter.Remaining > 0)
            {
                counter.Remaining -= 1;
                throw new InvalidOperationException("synthetic composing failure");
            }
            return Task.FromResult(PhaseHandlerResult.Advanced);
        }
    }

    private sealed class TestHostEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ThanyMarcus.Cloud.Api";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
