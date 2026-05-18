using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.PluginTokens;

public sealed class RetryPluginTokenEndpointTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task Returns_202_when_status_failed_plugin_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, job) = await SeedAsync(SagaStatus.FailedPluginToken, ct);
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<RetryPluginTokenResponse>(ct);
        body!.JobId.ShouldBe(job.Id);
        body.Status.ShouldBe(SagaStatus.IssuingPluginToken);

        Db.ChangeTracker.Clear();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.IssuingPluginToken);
        reloadedJob.AttemptCount.ShouldBe<short>(0);
        reloadedJob.LastError.ShouldBeNull();

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.IssuingPluginToken);
    }

    [Fact]
    public async Task Returns_409_when_status_not_failed_plugin_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, _) = await SeedAsync(SagaStatus.Succeeded, ct);
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Returns_404_for_unknown_cloud()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid()).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_403_when_wrong_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, cloud, _) = await SeedAsync(SagaStatus.FailedPluginToken, ct);
        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid()).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Double_retry_returns_409_on_second_call_after_status_already_issuing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, _) = await SeedAsync(SagaStatus.FailedPluginToken, ct);
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id).CreateClient();

        var first = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var second = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens/retry", UriKind.Relative),
            content: null, ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task<(User user, Cloud cloud, ProvisioningJob job)> SeedAsync(
        string status, CancellationToken ct)
    {
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"g-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            Name = "Test",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        var cloud = new Cloud
        {
            UserId = user.Id,
            Name = "test",
            Provider = "digitalocean",
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);

        var job = new ProvisioningJob
        {
            CloudId        = cloud.Id,
            UserId         = user.Id,
            Kind           = SagaKinds.Create,
            Payload        = JsonDocument.Parse("{}"),
            Status         = status,
            NextVisibleAt  = now,
            PhaseStartedAt = now,
            AttemptCount   = 3,
            LastError      = "some_prior_error",
            EventsLog      = JsonDocument.Parse("[]"),
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        Db.ProvisioningJobs.Add(job);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud, job);
    }
}
