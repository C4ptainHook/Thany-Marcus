using System.Net;
using System.Net.Http.Json;
using Shouldly;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Tests.Admin;

[Collection(PostgresCollection.Name)]
public sealed class AdminHealthEndpointTests(PostgresFixture postgres) : IDisposable
{
    private readonly string liveDir = Path.Combine(
        Path.GetTempPath(),
        "admin-health-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(liveDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task Returns_cert_ready_false_when_pem_files_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(liveDir);

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CertLiveDir      = liveDir,
        };
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
            new Uri("/admin/health", UriKind.Relative), ct);

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeFalse();
        resp.RegistrationStatus.ShouldBe(CloudAdminHealthResponse.RegistrationPending);
        resp.ApiVersion.ShouldBe(CloudAdminHealthResponse.CurrentApiVersion);
        resp.CloudId.ShouldBe(factory.CloudId);
    }

    [Fact]
    public async Task Returns_cert_ready_true_when_fullchain_and_privkey_present()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(liveDir);
        await File.WriteAllTextAsync(Path.Combine(liveDir, "fullchain.pem"), "x", ct);
        await File.WriteAllTextAsync(Path.Combine(liveDir, "privkey.pem"),   "y", ct);

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CertLiveDir      = liveDir,
        };
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
            new Uri("/admin/health", UriKind.Relative), ct);

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Returns_cert_ready_false_when_only_fullchain_present()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(liveDir);
        await File.WriteAllTextAsync(Path.Combine(liveDir, "fullchain.pem"), "x", ct);

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CertLiveDir      = liveDir,
        };
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
            new Uri("/admin/health", UriKind.Relative), ct);

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeFalse();
    }

    [Fact]
    public async Task Endpoint_requires_no_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(liveDir);

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CertLiveDir      = liveDir,
        };
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/admin/health", UriKind.Relative), ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
