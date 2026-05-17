using System.Net;
using System.Net.Http.Json;
using Shouldly;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.CloudAdmin;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Bootstrap;

[Collection(PostgresCollection.Name)]
public sealed class CertInstalledEndpointTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private readonly WireMockServer portal = WireMockServer.Start();
    private readonly string liveDir = Path.Combine(
        Path.GetTempPath(),
        "cert-installed-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(liveDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        portal.Stop();
        portal.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(liveDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task CertInstalled_for_our_hostname_triggers_callback_and_marks_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/api/clouds/callback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        await using var factory = new CloudApiFactory
        {
            ConnectionString  = postgres.ConnectionString,
            PortalCallbackUrl = portal.Urls[0] + "/api/clouds/callback",
            CertLiveDir       = liveDir,
        };
        using var client = factory.CreateClient();

        using var post = await client.PostAsJsonAsync(
            new Uri("/internal/cert-installed", UriKind.Relative),
            new { @event = "cert_installed", identifier = "test.thany.click" }, ct);

        post.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var resp = await PollUntilAsync(client, r => r.RegistrationStatus == CloudAdminHealthResponse.RegistrationRegistered, ct);

        resp.RegistrationStatus.ShouldBe(CloudAdminHealthResponse.RegistrationRegistered);
        portal.LogEntries.Count(e => e.RequestMessage.Path == "/api/clouds/callback").ShouldBe(1);
    }

    [Fact]
    public async Task CertInstalled_for_different_hostname_is_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/api/clouds/callback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        await using var factory = new CloudApiFactory
        {
            ConnectionString  = postgres.ConnectionString,
            PortalCallbackUrl = portal.Urls[0] + "/api/clouds/callback",
            CertLiveDir       = liveDir,
        };
        using var client = factory.CreateClient();

        using var post = await client.PostAsJsonAsync(
            new Uri("/internal/cert-installed", UriKind.Relative),
            new { @event = "cert_installed", identifier = "someone-else.example.com" }, ct);

        post.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        portal.LogEntries.Count(e => e.RequestMessage.Path == "/api/clouds/callback").ShouldBe(0);
    }

    [Fact]
    public async Task Duplicate_cert_installed_events_post_callback_once()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/api/clouds/callback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204).WithDelay(TimeSpan.FromMilliseconds(50)));

        await using var factory = new CloudApiFactory
        {
            ConnectionString  = postgres.ConnectionString,
            PortalCallbackUrl = portal.Urls[0] + "/api/clouds/callback",
            CertLiveDir       = liveDir,
        };
        using var client = factory.CreateClient();

        var body = new { @event = "cert_installed", identifier = "test.thany.click" };
        await Task.WhenAll(
            client.PostAsJsonAsync(new Uri("/internal/cert-installed", UriKind.Relative), body, ct),
            client.PostAsJsonAsync(new Uri("/internal/cert-installed", UriKind.Relative), body, ct),
            client.PostAsJsonAsync(new Uri("/internal/cert-installed", UriKind.Relative), body, ct));

        _ = await PollUntilAsync(client, r => r.RegistrationStatus == CloudAdminHealthResponse.RegistrationRegistered, ct);

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        portal.LogEntries.Count(e => e.RequestMessage.Path == "/api/clouds/callback").ShouldBe(1);
    }

    private static async Task<CloudAdminHealthResponse> PollUntilAsync(
        HttpClient client,
        Func<CloudAdminHealthResponse, bool> predicate,
        CancellationToken ct,
        int maxAttempts = 100,
        int delayMs = 50)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>(
                new Uri("/admin/health", UriKind.Relative), ct);
            if (resp is not null && predicate(resp)) return resp;
            await Task.Delay(delayMs, ct);
        }
        throw new TimeoutException("Predicate not satisfied within polling window");
    }
}
