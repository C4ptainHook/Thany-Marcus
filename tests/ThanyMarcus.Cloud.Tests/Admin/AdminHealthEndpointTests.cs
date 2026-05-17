using System.Net;
using System.Net.Http.Json;
using Shouldly;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.CloudAdmin;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Admin;

[Collection(PostgresCollection.Name)]
public sealed class AdminHealthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] AutomatedDomains = ["other.example.com", "test.thany.click"];

    private readonly WireMockServer caddy = WireMockServer.Start();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        caddy.Stop();
        caddy.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Returns_cert_ready_false_when_caddy_has_no_cert()
    {
        var ct = TestContext.Current.CancellationToken;
        caddy.Given(Request.Create().WithPath("/pki/ca/local/active-cert/test.thany.click").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        caddy.Given(Request.Create().WithPath("/config/apps/tls/certificates/automate").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CaddyAdminUrl = caddy.Urls[0],
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
    public async Task Returns_cert_ready_true_when_caddy_active_cert_responds_200()
    {
        var ct = TestContext.Current.CancellationToken;
        caddy.Given(Request.Create().WithPath("/pki/ca/local/active-cert/test.thany.click").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CaddyAdminUrl = caddy.Urls[0],
        };
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
            new Uri("/admin/health", UriKind.Relative), ct);

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Returns_cert_ready_true_when_certificates_automate_lists_hostname()
    {
        var ct = TestContext.Current.CancellationToken;
        caddy.Given(Request.Create().WithPath("/pki/ca/local/active-cert/test.thany.click").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        caddy.Given(Request.Create().WithPath("/config/apps/tls/certificates/automate").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(AutomatedDomains));

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CaddyAdminUrl = caddy.Urls[0],
        };
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
            new Uri("/admin/health", UriKind.Relative), ct);

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Endpoint_requires_no_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        caddy.Given(Request.Create().WithPath("/pki/ca/local/active-cert/test.thany.click").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        caddy.Given(Request.Create().WithPath("/config/apps/tls/certificates/automate").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        await using var factory = new CloudApiFactory
        {
            ConnectionString = postgres.ConnectionString,
            CaddyAdminUrl = caddy.Urls[0],
        };
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/admin/health", UriKind.Relative), ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
