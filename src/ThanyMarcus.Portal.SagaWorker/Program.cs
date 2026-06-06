using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Polly;
using Polly.Extensions.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "ThanyMarcus.Portal.SagaWorker",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
            .WithMetrics(m => m
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddConsoleExporter())
            .WithTracing(t => t
                .AddConsoleExporter());

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<TimestampInterceptor>();

        builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
            .UseNpgsql(
                builder.Configuration.GetConnectionString("Portal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured"),
                npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
            ?? throw new InvalidOperationException("DataProtection:KeyRingPath not configured (required for cross-process key sharing)");
        Directory.CreateDirectory(dpKeysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
            .SetApplicationName("ThanyMarcus.Portal");

        builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();
        builder.Services.AddScoped<ISagaCredentialGrantStore, PostgresSagaCredentialGrantStore>();
        builder.Services.AddScoped<ISagaCredentialSource, SagaCredentialSource>();
        builder.Services.AddScoped<IProviderTokenVault, ProviderTokenVault>();
        builder.Services.AddScoped<ICloudSecretBundle, CloudSecretBundle>();
        builder.Services.AddScoped<IDigitalOceanOAuthConnections, DigitalOceanOAuthConnections>();
        builder.Services.AddScoped<ICloudAdminTokenAccessor, CloudAdminTokenAccessor>();

        builder.Services.AddScoped<IProvisioningProvider, StubProvisioningProvider>();
        builder.Services.AddScoped<IProvisioningProvider, DigitalOceanProvisioningProvider>();
        builder.Services.AddScoped<IProvisioningProviderRegistry, ProvisioningProviderRegistry>();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient(DoSizesCatalog.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.digitalocean.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(2, n => TimeSpan.FromMilliseconds(200 * Math.Pow(3, n - 1))));
        builder.Services.AddSingleton<IDoSizesCatalog, DoSizesCatalog>();

        builder.Services.Configure<DigitalOceanOAuthOptions>(builder.Configuration.GetSection("DigitalOcean:OAuth"));
        builder.Services.AddHttpClient<IDigitalOceanOAuthClient, DigitalOceanOAuthClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(3, n => TimeSpan.FromMilliseconds(200 * Math.Pow(5, n - 1))));
        builder.Services.AddScoped<IProvisioningEventBus, PostgresProvisioningEventBus>();

        builder.Services.AddSingleton<ITerraformRunner, TerraformRunner>();
        builder.Services.AddSingleton<WorkspaceLayout>();

        builder.Services.Configure<CloudflareOptions>(builder.Configuration.GetSection("Cloudflare"));
        var cfOptions = builder.Configuration.GetSection("Cloudflare").Get<CloudflareOptions>() ?? new CloudflareOptions();
        builder.Services.AddHttpClient(CloudflareDnsClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddPolicyHandler(HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, n => TimeSpan.FromSeconds(Math.Pow(2, n - 1))));

        if (cfOptions.UseStub)
            builder.Services.AddSingleton<ICloudflareDnsClient, StubCloudflareDnsClient>();
        else
            builder.Services.AddSingleton<ICloudflareDnsClient, CloudflareDnsClient>();

        builder.Services.AddSingleton<ISpacesKeyProbe, AwsS3SpacesKeyProbe>();
        builder.Services.AddScoped<ISagaPhaseHandler, MintingSpacesHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, TfPlanningHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, TfApplyingHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, DnsCreatingHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, AwaitingCloudCallbackHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, AwaitingCertHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, IssuingPluginTokenHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, RollingBackTfHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, RollingBackDnsHandler>();
        builder.Services.AddScoped<ISagaPhaseHandler, DestroyEntryHandler>();

        builder.Services.AddScoped<IBlueGreenOperations, StubBlueGreenOperations>();
        builder.Services.AddScoped<IBlueGreenOperations, DigitalOceanBlueGreenOperations>();
        builder.Services.AddHttpClient(DigitalOceanBlueGreenOperations.HttpClientName, c =>
        {
            c.BaseAddress = new Uri("https://api.digitalocean.com/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        foreach (var migratePhase in new[]
                 {
                     SagaStatus.MigrateQuiescing, SagaStatus.MigrateSnapshotting, SagaStatus.MigrateProvisioning,
                     SagaStatus.MigrateVerifying, SagaStatus.MigrateCutover, SagaStatus.MigratePostGate,
                     SagaStatus.MigrateDestroyingOld,
                 })
        {
            var phase = migratePhase;
            builder.Services.AddScoped<ISagaPhaseHandler>(sp =>
                ActivatorUtilities.CreateInstance<BlueGreenHandler>(sp, phase));
        }

        builder.Services.AddScoped<CancelHandler>();
        builder.Services.AddScoped<SagaPhaseDispatcher>();

        builder.Services.AddHttpClient(AwaitingCertHandler.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));

        builder.Services.Configure<PluginTokenSyncOptions>(
            builder.Configuration.GetSection(PluginTokenSyncOptions.SectionName));
        builder.Services.AddSingleton<IPortalToCloudPluginTokenClient, PortalToCloudPluginTokenClient>();
        var pluginTokenOpts = builder.Configuration
            .GetSection(PluginTokenSyncOptions.SectionName)
            .Get<PluginTokenSyncOptions>() ?? new PluginTokenSyncOptions();
        builder.Services.AddHttpClient(PortalToCloudPluginTokenClient.HttpClientName, c =>
            c.Timeout = TimeSpan.FromSeconds(pluginTokenOpts.HttpTimeoutSeconds));

        builder.Services.AddHostedService<CrashRecoveryService>();
        builder.Services.AddHostedService<SagaCredentialGrantSweepService>();
        builder.Services.AddHostedService<SagaWorker>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
