using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Cancel;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.Cancel;

public sealed class CancelCloudEndpointTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task Unauthenticated_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithUnauthenticated().WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/cancel", UriKind.Relative),
            new CancelCloudRequest("anything"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Other_users_cloud_returns_403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, cloud) = await SeedCloudAsync(SagaStatus.TfPlanning, ct);
        var intruder = await InsertUserAsync(ct);
        var factory = Factory.WithTestAuth(intruder.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, intruder.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unknown_cloud_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync(ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/cancel", UriKind.Relative),
            new CancelCloudRequest("whatever"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Soft_deleted_cloud_returns_410()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(SagaStatus.TfPlanning, ct);
        var tracked = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        tracked.DestroyedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Succeeded_cloud_returns_409_use_destroy_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(SagaStatus.Succeeded, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("use_destroy_instead");
    }

    [Theory]
    [InlineData(SagaStatus.Cancelled)]
    [InlineData(SagaStatus.RolledBack)]
    public async Task Terminal_cancelled_or_rolled_back_returns_410(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(status, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Theory]
    [InlineData(SagaStatus.Destroying)]
    [InlineData(SagaStatus.FailedDestroy)]
    public async Task Not_cancellable_status_returns_409(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(status, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<NotCancellableBody>(ct);
        body!.Error.ShouldBe("not_cancellable");
        body.CurrentStatus.ShouldBe(status);
    }

    [Fact]
    public async Task Hostname_mismatch_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(SagaStatus.TfPlanning, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest("wrong.example.com"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("hostname_mismatch");
    }

    [Fact]
    public async Task Happy_path_enqueues_cancel_job_and_returns_202()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(SagaStatus.TfApplying, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await res.Content.ReadFromJsonAsync<AcceptedBody>(ct);
        body!.JobId.ShouldNotBe(Guid.Empty);
        body.CloudId.ShouldBe(cloud.Id);

        Db.ChangeTracker.Clear();
        var job = await Db.ProvisioningJobs.SingleAsync(j => j.Id == body.JobId, ct);
        job.Kind.ShouldBe(SagaKinds.Cancel);
        job.Status.ShouldBe(SagaStatus.Pending);
        job.CloudId.ShouldBe(cloud.Id);
        job.Payload.RootElement.GetProperty("reason").GetString().ShouldBe("user_initiated");
    }

    [Fact]
    public async Task Second_post_is_idempotent_and_returns_existing_cancel_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(SagaStatus.TfPlanning, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var first = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstBody = await first.Content.ReadFromJsonAsync<AcceptedBody>(ct);

        var second = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/cancel", UriKind.Relative),
            new CancelCloudRequest(cloud.Hostname), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var secondBody = await second.Content.ReadFromJsonAsync<AcceptedBody>(ct);

        secondBody!.JobId.ShouldBe(firstBody!.JobId);

        Db.ChangeTracker.Clear();
        var cancelJobs = await Db.ProvisioningJobs
            .Where(j => j.CloudId == cloud.Id && j.Kind == SagaKinds.Cancel)
            .ToListAsync(ct);
        cancelJobs.Count.ShouldBe(1);
    }

    private async Task<User> InsertUserAsync(CancellationToken ct)
    {
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid():N}@example.com",
            Name = "Alice",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private async Task<(User user, Cloud cloud)> SeedCloudAsync(string provisioningStatus, CancellationToken ct)
    {
        var user = await InsertUserAsync(ct);
        var now = Clock.GetCurrentInstant();
        var cloud = new Cloud
        {
            UserId = user.Id,
            Name = "c",
            Provider = "stub",
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = provisioningStatus,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud);
    }

    private static async Task SeedStepUpAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
        await cache.SetAsync(userId, RandomNumberGenerator.GetBytes(32), ct);
    }

    private sealed record ErrorBody(string Error);
    private sealed record NotCancellableBody(string Error, string CurrentStatus);
    private sealed record AcceptedBody(Guid JobId, Guid CloudId);
}
