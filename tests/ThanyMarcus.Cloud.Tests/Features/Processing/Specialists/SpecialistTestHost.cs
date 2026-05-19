using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Specialists;

public static class SpecialistTestHost
{
    public static ServiceProvider Build(
        string connectionString,
        IVlmClient? vlm = null,
        IDoclingClient? docling = null,
        IParakeetClient? parakeet = null,
        IUrlFetcherClient? url = null,
        IVideoSplitterClient? video = null,
        FakeArtifactStore? store = null,
        int maxAttempts = 3,
        IClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock ?? SystemClock.Instance);
        services.AddSingleton<TimestampInterceptor>();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton<IHostEnvironment>(new TestHostEnv());
        services.AddSingleton<IConfiguration>(_ =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Cloud"] = connectionString,
                ["IngestSaga:ExtractionTasks:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["IngestSaga:ExtractionTasks:BackoffSecondsBase"] = "1",
                ["IngestSaga:ExtractionTasks:LeaseSeconds"] = "60",
                ["IngestSaga:ExtractionTasks:IdlePollMs"] = "1000",
            }).Build());

        services.AddDbContext<CloudDbContext>((sp, opts) => opts
            .UseNpgsql(connectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        services.AddSingleton<IIngestEventBus, NoOpIngestEventBus>();
        services.AddScoped<AttachmentExtractionCache>();
        services.AddSingleton(vlm ?? new ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs.StubVlmClient() as IVlmClient);
        services.AddSingleton(docling ?? new InMemoryDoclingClient("# stub docling\n") as IDoclingClient);
        services.AddSingleton(parakeet ?? new InMemoryParakeetClient("[stub transcript]") as IParakeetClient);
        services.AddSingleton(url ?? new ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs.StubUrlFetcherClient() as IUrlFetcherClient);
        services.AddSingleton(video ?? new InMemoryVideoSplitterClient() as IVideoSplitterClient);

        services.AddSingleton<IDocumentPreflighter, AlwaysPassDocumentPreflighter>();
        services.AddSingleton<IAudioPreflighter, AlwaysPassAudioPreflighter>();

        services.AddSingleton(new VideoFilterOptions());
        services.AddSingleton<IArtifactStore>(store ?? new FakeArtifactStore());

        return services.BuildServiceProvider();
    }

    public static VlmWorker NewVlm(IServiceProvider sp) =>
        new(sp, sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<VlmWorker>>());

    public static DoclingWorker NewDocling(IServiceProvider sp) =>
        new(sp, sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<DoclingWorker>>());

    public static ParakeetWorker NewParakeet(IServiceProvider sp) =>
        new(sp, sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<ParakeetWorker>>());

    public static UrlFetcherWorker NewUrl(IServiceProvider sp) =>
        new(sp, sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<UrlFetcherWorker>>());

    public static VideoSplitterWorker NewVideo(IServiceProvider sp) =>
        new(sp, sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<VideoFilterOptions>(),
            sp.GetRequiredService<ILogger<VideoSplitterWorker>>());

    private sealed class TestHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ThanyMarcus.Cloud.Api";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
