using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class CloudApiFactory : WebApplicationFactory<Program>
{
    public Guid CloudId { get; } = Guid.CreateVersion7();
    public string Hostname { get; init; } = "test.thany.click";
    public string PortalCallbackUrl { get; set; } = "http://localhost:65535/api/clouds/callback";
    public string CertLiveDir { get; set; } = "/tmp/does-not-exist";
    public string ConnectionString { get; init; } = "";

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
            });
        });
    }
}
