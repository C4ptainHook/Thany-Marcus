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
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;
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
builder.Services.AddSingleton<IImageExtractor, NotImplementedImageExtractor>();
builder.Services.AddSingleton<IVoiceExtractor, NotImplementedVoiceExtractor>();
builder.Services.AddSingleton<IFileExtractor, NotImplementedFileExtractor>();

builder.Services.AddSingleton(_ =>
    builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions());
builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();

builder.Services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();
builder.Services.AddSingleton<IVlmClient, StubVlmClient>();
builder.Services.AddSingleton<IDoclingClient, StubDoclingClient>();
builder.Services.AddSingleton<IParakeetClient, StubParakeetClient>();
builder.Services.AddSingleton<IUrlFetcherClient, StubUrlFetcherClient>();
builder.Services.AddSingleton<IVideoSplitterClient, StubVideoSplitterClient>();

builder.Services.AddScoped<IIngestEventBus, PostgresIngestEventBus>();
builder.Services.AddSingleton<IngestSseTranslator>();
builder.Services.AddScoped<JobStateTransitions>();
builder.Services.AddScoped<ProvenanceMaterializer>();
builder.Services.AddScoped<IPhaseHandler, ExtractingAttachmentsHandler>();
builder.Services.AddScoped<IPhaseHandler, ComposingHandler>();
builder.Services.AddScoped<IPhaseHandler, RoutingHandler>();
builder.Services.AddScoped<IPhaseHandler, ExtractingEntitiesHandler>();
builder.Services.AddScoped<IPhaseHandler, EmbeddingHandler>();
builder.Services.AddScoped<CancelHandler>();
builder.Services.AddScoped<IngestPhaseDispatcher>();
builder.Services.AddHostedService<JobOrchestratorWorker>();
builder.Services.AddHostedService<OrphanIngestSweeper>();
builder.Services.AddHostedService<VlmWorker>();
builder.Services.AddHostedService<DoclingWorker>();
builder.Services.AddHostedService<ParakeetWorker>();
builder.Services.AddHostedService<UrlFetcherWorker>();
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

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<CloudDbContext>(
        name: "cloud_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

await ApplyMigrationsAsync(app);

app.UseForwardedHeaders();

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
app.MapReprocessEndpoint();
app.MapNoteDeleteEndpoint();
app.MapSyncPullEndpoint();
app.MapSyncEventsEndpoint();
app.MapAdminSettingsEndpoints();
app.MapAdminPluginTokenEndpoints();

app.MapFallback(() => Results.NotFound());

await app.RunAsync();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
    await db.Database.MigrateAsync();
}

public partial class Program;
