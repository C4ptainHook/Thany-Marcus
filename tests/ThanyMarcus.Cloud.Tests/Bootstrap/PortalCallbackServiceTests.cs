using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Bootstrap;
using ThanyMarcus.Shared.CloudAdmin;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Bootstrap;

public sealed class PortalCallbackServiceTests : IAsyncLifetime
{
    private readonly WireMockServer portal = WireMockServer.Start();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        portal.Stop();
        portal.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Successful_204_marks_state_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/cb").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var state = new BootstrapState();
        var svc = NewService(state);

        await svc.PostRegistrationAsync(ct);

        state.RegistrationStatus.ShouldBe(CloudAdminHealthResponse.RegistrationRegistered);
        state.RegisteredAt.ShouldNotBeNull();
        portal.LogEntries.Count(e => e.RequestMessage.Path == "/cb").ShouldBe(1);
    }

    [Fact]
    public async Task Permanent_400_marks_failed_and_does_not_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/cb").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        var state = new BootstrapState();
        var svc = NewService(state);

        await svc.PostRegistrationAsync(ct);

        state.RegistrationStatus.ShouldBe(CloudAdminHealthResponse.RegistrationFailed);
        portal.LogEntries.Count(e => e.RequestMessage.Path == "/cb").ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_calls_dedupe()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/cb").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204).WithDelay(TimeSpan.FromMilliseconds(150)));

        var state = new BootstrapState();
        var svc = NewService(state);

        await Task.WhenAll(
            svc.PostRegistrationAsync(ct),
            svc.PostRegistrationAsync(ct),
            svc.PostRegistrationAsync(ct));

        portal.LogEntries.Count(e => e.RequestMessage.Path == "/cb").ShouldBe(1);
        state.RegistrationStatus.ShouldBe(CloudAdminHealthResponse.RegistrationRegistered);
    }

    [Fact]
    public async Task After_registered_subsequent_calls_are_short_circuited()
    {
        var ct = TestContext.Current.CancellationToken;
        portal.Given(Request.Create().WithPath("/cb").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var state = new BootstrapState();
        var svc = NewService(state);

        await svc.PostRegistrationAsync(ct);
        await svc.PostRegistrationAsync(ct);
        await svc.PostRegistrationAsync(ct);

        portal.LogEntries.Count(e => e.RequestMessage.Path == "/cb").ShouldBe(1);
    }

    private PortalCallbackService NewService(BootstrapState state)
    {
        var opts = new BootstrapOptions
        {
            CloudId           = Guid.CreateVersion7(),
            Hostname          = "test.thany.click",
            EnrollmentToken   = "et",
            CloudAdminToken   = "cat",
            PortalCallbackUrl = portal.Urls[0] + "/cb",
        };
        var factory = new SingleClientFactory(TimeSpan.FromSeconds(10));
        var clock = new FakeClock(Instant.FromUtc(2026, 5, 17, 12, 0));
        return new PortalCallbackService(factory, opts, state, clock, NullLogger<PortalCallbackService>.Instance);
    }

    private sealed class SingleClientFactory(TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = timeout };
    }
}
