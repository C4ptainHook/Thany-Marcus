using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sweepers;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class CloudApiFactory : WebApplicationFactory<Program>
{
    public Guid CloudId { get; } = Guid.CreateVersion7();
    public string Hostname { get; init; } = "test.thany.click";
    public string PortalCallbackUrl { get; set; } = "http://localhost:65535/api/clouds/callback";
    public string CertLiveDir { get; set; } = "/tmp/does-not-exist";
    public string ConnectionString { get; init; } = "";

    public string StorageEndpoint { get; set; } = "http://localhost:65530";
    public string StorageRegion   { get; set; } = "us-east-1";
    public string StorageBucket   { get; set; } = "test-bucket";
    public string StorageAccessKeyId     { get; set; } = "test-key";
    public string StorageAccessKeySecret { get; set; } = "test-secret";

    public bool DisableHostedServices { get; init; } = true;
    public Action<IServiceCollection>? CustomizeServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Cloud"]       = ConnectionString,
                ["Cert:LiveDir"]                  = CertLiveDir,
                ["Bootstrap:CloudId"]             = CloudId.ToString(),
                ["Bootstrap:Hostname"]            = Hostname,
                ["Bootstrap:EnrollmentToken"]     = "test-enrollment-token",
                ["Bootstrap:CloudAdminToken"]     = "test-cloud-admin-token",
                ["Bootstrap:PortalCallbackUrl"]   = PortalCallbackUrl,
                ["Storage:Provider"]              = "s3",
                ["Storage:Endpoint"]              = StorageEndpoint,
                ["Storage:Region"]                = StorageRegion,
                ["Storage:Bucket"]                = StorageBucket,
                ["Storage:AccessKeyId"]           = StorageAccessKeyId,
                ["Storage:AccessKeySecret"]       = StorageAccessKeySecret,
                ["Llm:SystemPrompt"]              = "test system prompt",
                ["Llm:DefaultAnthropicModel"]     = "claude-haiku-4-5-20251001",
                ["Llm:BodySectionHeader"]         = "## Body",
                ["Llm:AttachmentsSectionHeader"]  = "## Attachments",
                ["Llm:AttachmentHeaderTemplate"]  = "### Attachment {0} (kind={1})",
            });
        });

        builder.ConfigureServices(services =>
        {
            if (DisableHostedServices)
            {
                services.RemoveAll<IHostedService>(s =>
                    s.ImplementationType == typeof(IngestSagaWorker) ||
                    s.ImplementationType == typeof(OrphanIngestSweeper));
            }
            CustomizeServices?.Invoke(services);
        });
    }
}

internal static class ServiceCollectionRemoveExtensions
{
    public static void RemoveAll<TService>(this IServiceCollection services, Func<ServiceDescriptor, bool> predicate)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService) && predicate(services[i]))
            {
                services.RemoveAt(i);
            }
        }
    }
}
