using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodaTime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using ThanyMarcus.Cloud.Api.Features.Admin.Health;
using ThanyMarcus.Cloud.Api.Features.Admin.PluginTokens;
using ThanyMarcus.Cloud.Api.Features.Bootstrap;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Features.Recovery;
using ThanyMarcus.Cloud.Api.Features.Update;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;
using ThanyMarcus.Shared.Database;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes  = true;
    o.UseUtcTimestamp = true;
});

builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Cloud.Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<TimestampInterceptor>();

builder.Services.AddDbContext<CloudDbContext>((sp, opts) => opts
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured"),
        npg => npg.UseNodaTime().UseVector())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

builder.Services.AddSingleton(_ => new BootstrapOptions
{
    CloudId           = Guid.Parse(builder.Configuration["Bootstrap:CloudId"]
                                   ?? throw new InvalidOperationException("Bootstrap:CloudId required")),
    Hostname          = builder.Configuration["Bootstrap:Hostname"]
                        ?? throw new InvalidOperationException("Bootstrap:Hostname required"),
    EnrollmentToken   = builder.Configuration["Bootstrap:EnrollmentToken"]
                        ?? throw new InvalidOperationException("Bootstrap:EnrollmentToken required"),
    CloudAdminToken   = builder.Configuration["Bootstrap:CloudAdminToken"]
                        ?? throw new InvalidOperationException("Bootstrap:CloudAdminToken required"),
    PortalCallbackUrl = builder.Configuration["Bootstrap:PortalCallbackUrl"]
                        ?? throw new InvalidOperationException("Bootstrap:PortalCallbackUrl required"),
});
builder.Services.AddSingleton<BootstrapState>();
builder.Services.AddHttpClient(PortalCallbackService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<PortalCallbackService>();

builder.Services.AddSingleton<CertFileReader>();
builder.Services.AddHostedService<RegistrationStartupSweeper>();

builder.Services.Configure<CloudUpdateOptions>(builder.Configuration.GetSection(CloudUpdateOptions.SectionName));
builder.Services.AddSingleton<CloudVersionReader>();
builder.Services.AddSingleton<CloudUpdateStateStore>();
builder.Services.AddSingleton<RecoveryThrottle>();

builder.Services.AddSingleton(_ => new StorageOptions
{
    Provider        = builder.Configuration["Storage:Provider"]        ?? StorageProviders.S3,
    Endpoint        = builder.Configuration["Storage:Endpoint"]        ?? "",
    Region          = builder.Configuration["Storage:Region"]          ?? "",
    Bucket          = builder.Configuration["Storage:Bucket"]          ?? "",
    AccessKeyId     = builder.Configuration["Storage:AccessKeyId"]     ?? "",
    AccessKeySecret = builder.Configuration["Storage:AccessKeySecret"] ?? "",
});

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var o = sp.GetRequiredService<StorageOptions>();
    var creds = new BasicAWSCredentials(o.AccessKeyId, o.AccessKeySecret);
    var cfg = new AmazonS3Config
    {
        ServiceURL     = o.Endpoint,
        AuthenticationRegion = o.Region,
        ForcePathStyle = false,
    };
    return new AmazonS3Client(creds, cfg);
});

builder.Services.AddSingleton<IArtifactStore, S3ArtifactStore>();

builder.Services.AddScoped<IPluginTokenAuthenticator, PluginTokenAuthenticator>();
builder.Services.AddScoped<RequirePluginAuthFilter>();
builder.Services.AddScoped<RequireCloudAdminTokenFilter>();

builder.Services.AddHttpClient(UrlExtractor.HttpClientName);
builder.Services.AddSingleton<IUrlExtractor, UrlExtractor>();

builder.Services.AddHttpClient(RealUrlFetcherClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("IngestSaga:Sidecars:Url:GetTimeoutSeconds", 10));
});
builder.Services.AddHttpClient(OllamaClientNames.Vlm, c =>
{
    c.BaseAddress = new Uri(
        builder.Configuration["IngestSaga:Sidecars:OllamaVision:BaseUrl"]
            ?? "http://localhost:11434");
    c.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("IngestSaga:Sidecars:OllamaVision:RequestTimeoutSeconds", 180));
});
builder.Services.AddHttpClient(OllamaClientNames.Text, c =>
{
    c.BaseAddress = new Uri(
        builder.Configuration["IngestSaga:Sidecars:OllamaText:BaseUrl"]
            ?? "http://localhost:11434");
    c.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("IngestSaga:Sidecars:OllamaText:RequestTimeoutSeconds", 60));
});

builder.Services.AddSingleton(_ => new DoclingOptions
{
    BaseUrl              = builder.Configuration["IngestSaga:Sidecars:Docling:BaseUrl"] ?? "http://docling:5001",
    HealthPath           = builder.Configuration["IngestSaga:Sidecars:Docling:HealthPath"] ?? "/health",
    ConvertSourcePath    = builder.Configuration["IngestSaga:Sidecars:Docling:ConvertSourcePath"] ?? "/v1alpha/convert/source",
    RequestTimeoutSeconds = builder.Configuration.GetValue("IngestSaga:Sidecars:Docling:RequestTimeoutSeconds", 120),
});
builder.Services.AddHttpClient(DoclingHttpClient.HttpClientName, (sp, c) =>
{
    var o = sp.GetRequiredService<DoclingOptions>();
    c.BaseAddress = new Uri(o.BaseUrl);
    c.Timeout     = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
});

builder.Services.AddSingleton(_ => new DocumentFilterOptions
{
    MaxSizeBytes = builder.Configuration.GetValue("IngestSaga:Filters:Document:MaxSizeBytes", 52428800L),
    MaxPageCount = builder.Configuration.GetValue("IngestSaga:Filters:Document:MaxPageCount", 200),
});
builder.Services.AddSingleton<IDocumentPreflighter, PdfPreflighter>();

builder.Services.AddSingleton(_ => new ParakeetOptions
{
    BaseUrl              = builder.Configuration["IngestSaga:Sidecars:Parakeet:BaseUrl"] ?? "http://parakeet:5092",
    HealthPath           = builder.Configuration["IngestSaga:Sidecars:Parakeet:HealthPath"] ?? "/health",
    TranscribePath       = builder.Configuration["IngestSaga:Sidecars:Parakeet:TranscribePath"] ?? "/v1/audio/transcriptions",
    RequestTimeoutSeconds = builder.Configuration.GetValue("IngestSaga:Sidecars:Parakeet:RequestTimeoutSeconds", 180),
});
builder.Services.AddHttpClient(ParakeetHttpClient.HttpClientName, (sp, c) =>
{
    var o = sp.GetRequiredService<ParakeetOptions>();
    c.BaseAddress = new Uri(o.BaseUrl);
    c.Timeout     = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
});

builder.Services.AddSingleton(_ => new AudioFilterOptions
{
    MaxSizeBytes        = builder.Configuration.GetValue("IngestSaga:Filters:Audio:MaxSizeBytes", 209_715_200L),
    MaxDurationSeconds  = builder.Configuration.GetValue("IngestSaga:Filters:Audio:MaxDurationSeconds", 600),
    SilenceRmsThreshold = builder.Configuration.GetValue("IngestSaga:Filters:Audio:SilenceRmsThreshold", 0.005f),
    SilenceWindowMs     = builder.Configuration.GetValue("IngestSaga:Filters:Audio:SilenceWindowMs", 100),
});

builder.Services.AddSingleton(_ => new VideoFilterOptions
{
    MaxDurationSeconds      = builder.Configuration.GetValue("IngestSaga:Filters:Video:MaxDurationSeconds", 300),
    KeyframeIntervalSeconds = builder.Configuration.GetValue("IngestSaga:Filters:Video:KeyframeIntervalSeconds", 5),
    MaxKeyframes            = builder.Configuration.GetValue("IngestSaga:Filters:Video:MaxKeyframes", 60),
    JpegQuality             = builder.Configuration.GetValue("IngestSaga:Filters:Video:JpegQuality", 85),
});

builder.Services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
builder.Services.AddSingleton<IFfprobeRunner, FfprobeRunner>();
builder.Services.AddSingleton<IAudioPreflighter, AudioPreflighter>();

builder.Services.AddHttpClient(FfmpegVideoSplitterClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue("IngestSaga:Sidecars:VideoSplitter:DownloadTimeoutMinutes", 5));
});

builder.Services.AddScoped<AttachmentExtractionCache>();
builder.Services.AddSingleton<IImageExtractor, NotImplementedImageExtractor>();
builder.Services.AddSingleton<IVoiceExtractor, NotImplementedVoiceExtractor>();
builder.Services.AddSingleton<IFileExtractor, NotImplementedFileExtractor>();

builder.Services.Configure<LlmIntelligenceOptions>(builder.Configuration.GetSection("LlmIntelligence"));
builder.Services.AddSingleton<SafeLlmClient>();
builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<SafeLlmClient>());
builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
builder.Services.AddScoped<LlmEventAppender>();

builder.Services.Configure<GraniteEmbeddingOptions>(
    builder.Configuration.GetSection("IngestSaga:Models:Embedding"));
builder.Services.AddSingleton<IEmbeddingClient, GraniteEmbeddingClient>();
builder.Services.AddHostedService<GraniteEmbeddingWarmupService>();

builder.Services.Configure<QueryEmbeddingCacheOptions>(
    builder.Configuration.GetSection("IngestSaga:Retrieval:QueryCache"));
builder.Services.AddSingleton<QueryEmbeddingCache>();
builder.Services.Configure<RelatedNotesOptions>(
    builder.Configuration.GetSection("IngestSaga:Retrieval"));
builder.Services.AddSingleton<RelatedNotesCalibrationSignal>();
builder.Services.AddScoped<RelatedNotesCalibrator>();
builder.Services.AddScoped<ReindexGate>();
builder.Services.AddHostedService<RelatedNotesCalibrationWorker>();
builder.Services.AddScoped<IVlmClient, OllamaVlmClient>();
builder.Services.AddSingleton<IDoclingClient, DoclingHttpClient>();
builder.Services.AddSingleton<IParakeetClient, ParakeetHttpClient>();
builder.Services.AddScoped<IUrlFetcherClient, RealUrlFetcherClient>();
builder.Services.AddSingleton<IVideoSplitterClient, FfmpegVideoSplitterClient>();

builder.Services.AddScoped<IIngestEventBus, PostgresIngestEventBus>();
builder.Services.AddSingleton<IngestSseTranslator>();
builder.Services.AddScoped<JobStateTransitions>();
builder.Services.AddScoped<ProvenanceMaterializer>();

builder.Services.AddSingleton<IAttachmentRenderer, UrlRenderer>();
builder.Services.AddSingleton<IAttachmentRenderer, ImageRenderer>();
builder.Services.AddSingleton<IAttachmentRenderer, AudioRenderer>();
builder.Services.AddSingleton<IAttachmentRenderer, VideoRenderer>();
builder.Services.AddSingleton<IAttachmentRenderer, DocumentRenderer>();
builder.Services.AddSingleton<FailedHiddenRenderer>();
builder.Services.AddSingleton<CompositeNoteComposer>();

builder.Services.AddScoped<EntitySuggestionAggregator>();
builder.Services.AddScoped<EntityStubWriter>();
builder.Services.AddScoped<EntitySuggestionRepository>();
builder.Services.AddScoped<IPhaseHandler, ExtractingAttachmentsHandler>();
builder.Services.AddScoped<IPhaseHandler, RoutingHandler>();
builder.Services.AddScoped<IPhaseHandler, ExtractingEntitiesHandler>();
builder.Services.AddScoped<IPhaseHandler, SynthesizingHandler>();
builder.Services.AddScoped<IPhaseHandler, EmbeddingHandler>();

builder.Services.AddHttpClient(GoogleGeminiClient.HttpClientName, c =>
{
    c.BaseAddress = new Uri(
        builder.Configuration["IngestSaga:Synthesis:Google:BaseUrl"]
            ?? "https://generativelanguage.googleapis.com");
    c.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("IngestSaga:Synthesis:Google:RequestTimeoutSeconds", 120));
});
builder.Services.AddSingleton<OllamaSynthesisLlmClient>();
builder.Services.AddSingleton<GoogleGeminiClient>();
builder.Services.AddSingleton<ISynthesisLlmRouter, SynthesisLlmRouter>();
builder.Services.AddSingleton<ISynthesisApiKeyStore, InMemorySynthesisApiKeyStore>();
builder.Services.AddScoped<CancelHandler>();
builder.Services.AddScoped<HubGenerationHandler>();
builder.Services.AddScoped<IngestPhaseDispatcher>();
builder.Services.AddScoped<FolderRouter>();
builder.Services.AddScoped<ThanyMarcus.Cloud.Api.Features.Sync.NoteTombstoneService>();
builder.Services.AddScoped<ThanyMarcus.Cloud.Api.Features.Sync.InboxRerouteService>();
builder.Services.AddSingleton<ThanyMarcus.Cloud.Api.Features.Sync.InboxRerouteSignal>();
builder.Services.AddHostedService<ThanyMarcus.Cloud.Api.Features.Sync.InboxRerouteWorker>();
builder.Services.AddHostedService<JobOrchestratorWorker>();
builder.Services.AddHostedService<OrphanIngestSweeper>();
builder.Services.AddHostedService<TombstoneGcSweeper>();
builder.Services.AddHostedService<VlmWorker>();
builder.Services.AddHostedService<DoclingWorker>();
builder.Services.AddHostedService<ParakeetWorker>();
builder.Services.AddHostedService<UrlFetcherWorker>();
builder.Services.AddHostedService<UrlMetadataWorker>();
builder.Services.AddHostedService<VideoSplitterWorker>();

var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("ThanyMarcus.Cloud");

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
});

const string PluginCorsPolicy = "PluginOrigin";
builder.Services.AddCors(o =>
{
    var origins = new List<string> { "app://obsidian.md", "capacitor://localhost" };
    var portalCallback = builder.Configuration["Bootstrap:PortalCallbackUrl"];
    if (!string.IsNullOrWhiteSpace(portalCallback)
        && Uri.TryCreate(portalCallback, UriKind.Absolute, out var portalUri))
    {
        origins.Add($"{portalUri.Scheme}://{portalUri.Authority}");
    }
    o.AddPolicy(PluginCorsPolicy, p => p
        .WithOrigins([.. origins])
        .AllowAnyHeader()
        .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
        .AllowCredentials());
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<CloudDbContext>(
        name: "cloud_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

await ApplyMigrationsAsync(app);

app.UseForwardedHeaders();

app.UseCors(PluginCorsPolicy);

app.MapOpenApi();
app.MapScalarApiReference();
app.MapPrometheusScrapingEndpoint();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.MapAdminHealthEndpoint();
app.MapCertInstalledEndpoint();

app.MapIngestEndpoints();
app.MapListJobsEndpoint();
app.MapReprocessEndpoint();
app.MapNoteDeleteEndpoint();
app.MapRelatedNotesEndpoint();
app.MapCancelIngestEndpoint();
app.MapSyncPullEndpoint();
app.MapSyncPushEndpoint();
app.MapSyncEventsEndpoint();
app.MapSyncMaintenanceEndpoints();
app.MapFolderDissolveEndpoint();
app.MapEntitySuggestionsEndpoints();
app.MapAdminSettingsEndpoints();
app.MapAdminPluginTokenEndpoints();
app.MapPluginTokenRotationEndpoint();
app.MapRecoveryEndpoints();
app.MapUpdateEndpoints();

app.MapFallback(() => Results.NotFound());

await app.RunAsync();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
    await db.Database.MigrateAsync();
}

public partial class Program;
