using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class AwaitingCertHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Cert_ready_transitions_to_issuing_plugin_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCert,
            ct: ct);

        var handler = new AwaitingCertHandler(
            Db, Clock,
            BuildHttpFactory(returnCertReady: true),
            new StubAdminTokenAccessor(),
            NullLogger<AwaitingCertHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.IssuingPluginToken);
        _ = cloud;
    }

    [Fact]
    public async Task Cert_not_ready_reschedules_at_fast_cadence_when_young()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCert,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.PhaseStartedAt = Clock.GetCurrentInstant() - Duration.FromSeconds(10);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new AwaitingCertHandler(
            Db, Clock,
            BuildHttpFactory(returnCertReady: false),
            new StubAdminTokenAccessor(),
            NullLogger<AwaitingCertHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.AwaitingCert);
        (reloaded.NextVisibleAt - Clock.GetCurrentInstant()).TotalSeconds.ShouldBeInRange(4, 6);
    }

    [Fact]
    public async Task Deadline_exceeded_transitions_to_failed_cert()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCert,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.PhaseStartedAt = Clock.GetCurrentInstant() - Duration.FromMinutes(31);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new AwaitingCertHandler(
            Db, Clock,
            BuildHttpFactory(returnCertReady: false),
            new StubAdminTokenAccessor(),
            NullLogger<AwaitingCertHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedCert);
    }

    private static ScriptedHttpFactory BuildHttpFactory(bool returnCertReady) =>
        new(returnCertReady);

    private sealed class StubAdminTokenAccessor : ICloudAdminTokenAccessor
    {
        public Task<string?> GetPlaintextAsync(Guid cloudId, CancellationToken ct)
            => Task.FromResult<string?>("stub-cloud-admin-token");
    }

    private sealed class ScriptedHttpFactory(bool certReady) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ScriptedHandler(certReady));
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
