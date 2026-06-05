using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

public static class ProcessingTestHost
{
    public static ServiceProvider Build(string connectionString, IClock? clock = null,
        Action<IServiceCollection>? customize = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock ?? SystemClock.Instance);
        services.AddSingleton<TimestampInterceptor>();
        services.AddLogging(b => b.AddDebug());
        services.AddDataProtection();

        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());

        services.AddSingleton<IConfiguration>(sp =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Cloud"] = connectionString,
                    ["IngestSaga:Orchestrator:MaxConcurrentJobs"] = "3",
                    ["IngestSaga:Orchestrator:LeaseSeconds"] = "60",
                    ["IngestSaga:Orchestrator:HeartbeatSeconds"] = "20",
                    ["IngestSaga:Orchestrator:IdlePollMs"] = "60000",
                    ["IngestSaga:Models:Stub:Version"] = "stub-v1",
                    ["IngestSaga:Phases:extracting_attachments:MaxAttempts"] = "1",
                    ["IngestSaga:Phases:composing:MaxAttempts"] = "2",
                    ["IngestSaga:Phases:routing:MaxAttempts"] = "3",
                    ["IngestSaga:Phases:extracting_entities:MaxAttempts"] = "3",
                    ["IngestSaga:Phases:embedding:MaxAttempts"] = "3",
                    ["IngestSaga:ExtractionTasks:MaxAttempts"] = "3",
                    ["IngestSaga:ExtractionTasks:BackoffSecondsBase"] = "2",
                    ["IngestSaga:ExtractionTasks:LeaseSeconds"] = "60",
                    ["IngestSaga:ExtractionTasks:IdlePollMs"] = "60000",
                    ["IngestSaga:Models:Vlm:OllamaTag"] = "minicpm-v:8b-2.6-q4_K_M",
                    ["IngestSaga:Models:Route:OllamaTag"] = "qwen3:1.7b-q4_K_M",
                    ["IngestSaga:Models:Entity:OllamaTag"] = "qwen3:1.7b-q4_K_M",
                    ["IngestSaga:Sidecars:OllamaVision:BaseUrl"] = "http://localhost:11434",
                    ["IngestSaga:Sidecars:OllamaText:BaseUrl"] = "http://localhost:11434",
                })
                .Build());

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

        services.AddSingleton<IArtifactStore, FakeArtifactStore>();

        services.AddSingleton<IAttachmentRenderer, UrlRenderer>();
        services.AddSingleton<IAttachmentRenderer, ImageRenderer>();
        services.AddSingleton<IAttachmentRenderer, AudioRenderer>();
        services.AddSingleton<IAttachmentRenderer, VideoRenderer>();
        services.AddSingleton<IAttachmentRenderer, DocumentRenderer>();
        services.AddSingleton<FailedHiddenRenderer>();
        services.AddSingleton<CompositeNoteComposer>();

        services.AddScoped<JobStateTransitions>();
        services.AddScoped<ProvenanceMaterializer>();
        services.AddScoped<FolderRouter>();
        services.AddScoped<ThanyMarcus.Cloud.Api.Features.EntitySuggestions.EntitySuggestionAggregator>();
        services.AddScoped<ThanyMarcus.Cloud.Api.Features.EntitySuggestions.EntityStubWriter>();
        services.AddScoped<IPhaseHandler, ExtractingAttachmentsHandler>();
        services.AddScoped<IPhaseHandler, ComposingHandler>();
        services.AddScoped<IPhaseHandler, RoutingHandler>();
        services.AddScoped<IPhaseHandler, ExtractingEntitiesHandler>();
        services.AddScoped<IPhaseHandler, EmbeddingHandler>();
        services.AddScoped<CancelHandler>();
        services.AddScoped<HubGenerationHandler>();
        services.AddScoped<IngestPhaseDispatcher>();

        customize?.Invoke(services);

        return services.BuildServiceProvider();
    }

    public static JobOrchestratorWorker NewOrchestrator(IServiceProvider sp) =>
        new(
            sp,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<JobOrchestratorWorker>>());

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ThanyMarcus.Cloud.Api";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public sealed class TestClock : IClock
{
    private Instant current;
    public TestClock(Instant start) { current = start; }
    public Instant GetCurrentInstant() => current;
    public void Advance(Duration d) => current += d;
    public void Set(Instant t) => current = t;
}
