using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;
using SagaWorkerService = ThanyMarcus.Portal.SagaWorker.SagaWorker;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

internal static class SagaHostBuilder
{
    public static IHost Build(
        string connectionString,
        string dpKeysDir,
        string workspaceBase,
        ITerraformRunner runner,
        ICloudflareDnsClient cloudflare,
        int maxConcurrent = 3,
        bool certPollReady = true,
        IDataProtectionProvider? dataProtectionProvider = null,
        IClock? clock = null,
        IPortalToCloudPluginTokenClient? pluginTokenCloudClient = null,
        Action<Dictionary<string, string?>>? extraConfig = null)
    {
        var builder = Host.CreateApplicationBuilder();

        var cfg = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Portal"] = connectionString,
            ["DataProtection:KeyRingPath"] = dpKeysDir,
            ["Provisioning:WorkspaceBase"] = workspaceBase,
            ["Provisioning:TerraformModulesDir"] = workspaceBase,
            ["Provisioning:DefaultSize"] = "stub-size",
            ["Provisioning:MaxConcurrentJobs"] = maxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Provisioning:IdlePollMs"] = "100",
            ["Provisioning:LeaseSeconds"] = "120",
        };
        extraConfig?.Invoke(cfg);
        builder.Configuration.AddInMemoryCollection(cfg);

        builder.Services.AddSingleton(clock ?? SystemClock.Instance);
        builder.Services.AddSingleton<TimestampInterceptor>();
        builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
            .UseNpgsql(connectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        if (dataProtectionProvider is not null)
        {
            builder.Services.AddSingleton(dataProtectionProvider);
        }
        else
        {
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
                .SetApplicationName("ThanyMarcus.Portal");
        }

        builder.Services.AddSingleton<IInfraOpUnlockCache>(new StubInfraOpUnlockCache());
        builder.Services.AddScoped<ISagaCredentialGrantStore, PostgresSagaCredentialGrantStore>();
        builder.Services.AddScoped<ISagaCredentialSource, SagaCredentialSource>();
        builder.Services.AddScoped<IProviderTokenVault, ProviderTokenVault>();
        builder.Services.AddScoped<ICloudSecretBundle, CloudSecretBundle>();
        builder.Services.AddScoped<IDigitalOceanOAuthConnections, DigitalOceanOAuthConnections>();
        builder.Services.AddSingleton<IDigitalOceanOAuthClient, FakeDigitalOceanOAuthClient>();
        builder.Services.AddScoped<ICloudAdminTokenAccessor, CloudAdminTokenAccessor>();
        builder.Services.AddScoped<IProvisioningProvider, StubProvisioningProvider>();
        builder.Services.AddScoped<IProvisioningProvider, DigitalOceanProvisioningProvider>();
        builder.Services.AddScoped<IProvisioningProviderRegistry, ProvisioningProviderRegistry>();
        builder.Services.AddScoped<IProvisioningEventBus, PostgresProvisioningEventBus>();
        builder.Services.AddSingleton(pluginTokenCloudClient ?? new StubPluginTokenCloudClient());
        builder.Services.Configure<PluginTokenSyncOptions>(o => { o.MaxAttempts = 3; o.HttpTimeoutSeconds = 10; });

        builder.Services.AddSingleton(runner);
        builder.Services.AddSingleton(cloudflare);
        builder.Services.AddSingleton<WorkspaceLayout>();

        builder.Services.AddSingleton<ISpacesKeyProbe, InstantSpacesKeyProbe>();
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
        builder.Services.AddScoped<CancelHandler>();
        builder.Services.AddScoped<SagaPhaseDispatcher>();

        builder.Services.AddSingleton<IHttpClientFactory>(_ => new ScriptedHttpFactory(certPollReady));
        builder.Services.AddSingleton<IDoSizesCatalog>(new NoopDoSizesCatalog());

        builder.Services.AddHostedService<CrashRecoveryService>();
        builder.Services.AddHostedService<SagaWorkerService>();

        return builder.Build();
    }

    private sealed class NoopDoSizesCatalog : IDoSizesCatalog
    {
        public Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct) =>
            Task.FromResult<DoSize?>(null);
    }

    private sealed class ScriptedHttpFactory(bool certReady) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ScriptedHandler(certReady));
    }

    private sealed class StubPluginTokenCloudClient : IPortalToCloudPluginTokenClient
    {
        public Task<Guid> PostAsync(string cloudUrl, string cloudAdminToken,
            byte[] tokenHashBytes, string label, CancellationToken ct)
            => Task.FromResult(Guid.CreateVersion7());

        public Task RevokeAsync(string cloudUrl, string cloudAdminToken,
            byte[] tokenHashBytes, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ScriptedHandler(bool certReady) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = certReady ? "{\"cert_ready\":true}" : "{\"cert_ready\":false}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
