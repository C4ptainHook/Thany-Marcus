using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class DnsCreatingHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Success_transitions_to_awaiting_cloud_callback_and_records_dns_record_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.DnsCreating,
            seedCloudflareToken: true,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.TfOutputs?.Dispose();
        tracked.TfOutputs = JsonDocument.Parse("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}");
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cf = new FakeCloudflareDnsClient();
        var handler = BuildHandler(dp, cf);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.AwaitingCloudCallback);
        cf.Creates.ShouldHaveSingleItem();
        reloaded.EventsLog.RootElement.GetRawText().ShouldContain("record_id");
    }

    [Fact]
    public async Task Cloudflare_failure_transitions_to_rolling_back_tf()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.DnsCreating,
            seedCloudflareToken: true,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.TfOutputs?.Dispose();
        tracked.TfOutputs = JsonDocument.Parse("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}");
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cf = new FakeCloudflareDnsClient { ThrowOnCreate = true };
        var handler = BuildHandler(dp, cf);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackTf);
    }

    private DnsCreatingHandler BuildHandler(IDataProtectionProvider dp, FakeCloudflareDnsClient cf)
    {
        _ = dp;
        return new DnsCreatingHandler(Db, Clock, cf, NullLogger<DnsCreatingHandler>.Instance);
    }
}
