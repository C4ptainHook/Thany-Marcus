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
using ThanyMarcus.Cloud.Api.Features.Bootstrap;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
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
        npg => npg.UseNodaTime())
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

app.MapFallback(() => Results.NotFound());

await app.RunAsync();

public partial class Program;
