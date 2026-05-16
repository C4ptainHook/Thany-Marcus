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
using ThanyMarcus.Portal.Api.Features.CloudManagement.Destroy;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.Destroy;

public sealed class DestroyCloudEndpointTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task Unauthenticated_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithUnauthenticated().WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/destroy", UriKind.Relative),
            new DestroyCloudRequest("anything"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_step_up_returns_401_step_up_required()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedSucceededCloudAsync(ct);
        using var client = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("step_up_required");
    }

    [Fact]
    public async Task Hostname_mismatch_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedSucceededCloudAsync(ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest("wrong.example.com"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("hostname_mismatch");
    }

    [Fact]
    public async Task Other_users_cloud_returns_404_or_403()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, cloud) = await SeedSucceededCloudAsync(ct);
        var intruder = await InsertUserAsync(ct);
        var factory = Factory.WithTestAuth(intruder.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, intruder.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

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
            new Uri($"/api/clouds/{Guid.NewGuid()}/destroy", UriKind.Relative),
            new DestroyCloudRequest("whatever"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Already_soft_deleted_cloud_returns_410()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedSucceededCloudAsync(ct);
        var tracked = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        tracked.DestroyedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Active_job_returns_409_cloud_busy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedSucceededCloudAsync(ct);
        Db.ProvisioningJobs.Add(new ProvisioningJob
        {
            CloudId = cloud.Id,
            UserId = user.Id,
            Kind = SagaKinds.Create,
            Payload = JsonDocument.Parse("{}"),
            Status = SagaStatus.TfApplying,
            NextVisibleAt = Clock.GetCurrentInstant(),
            AttemptCount = 1,
            EventsLog = JsonDocument.Parse("[]"),
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<ConflictBody>(ct);
        body!.Reason.ShouldBe("cloud_busy");
        body.CurrentPhase.ShouldBe(SagaStatus.TfApplying);
    }

    [Fact]
    public async Task Happy_path_enqueues_destroying_row_and_returns_202()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedSucceededCloudAsync(ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await res.Content.ReadFromJsonAsync<AcceptedBody>(ct);
        body!.JobId.ShouldNotBe(Guid.Empty);
        body.CloudId.ShouldBe(cloud.Id);

        Db.ChangeTracker.Clear();
        var job = await Db.ProvisioningJobs.SingleAsync(j => j.Id == body.JobId, ct);
        job.Kind.ShouldBe(SagaKinds.Destroy);
        job.Status.ShouldBe(SagaStatus.Destroying);
        job.CloudId.ShouldBe(cloud.Id);
        job.Payload.RootElement.GetProperty("reason").GetString().ShouldBe("user_initiated");
    }

    [Theory]
    [InlineData(SagaStatus.Succeeded, HttpStatusCode.Accepted)]
    [InlineData(SagaStatus.AwaitingCert, HttpStatusCode.Accepted)]
    [InlineData(SagaStatus.FailedCert, HttpStatusCode.Accepted)]
    [InlineData(SagaStatus.FailedTf, HttpStatusCode.Gone)]
    [InlineData(SagaStatus.FailedDns, HttpStatusCode.Gone)]
    [InlineData(SagaStatus.FailedCallback, HttpStatusCode.Gone)]
    [InlineData(SagaStatus.Cancelled, HttpStatusCode.Gone)]
    [InlineData(SagaStatus.RolledBack, HttpStatusCode.Gone)]
    public async Task Destroyability_check_branches_on_provisioning_status(string status, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud) = await SeedCloudAsync(status, ct);
        var factory = Factory.WithTestAuth(user.Id, totp: TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, ct);

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/destroy", UriKind.Relative),
            new DestroyCloudRequest(cloud.Hostname), ct);

        res.StatusCode.ShouldBe(expected);
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

    private async Task<(User user, Cloud cloud)> SeedSucceededCloudAsync(CancellationToken ct)
        => await SeedCloudAsync(SagaStatus.Succeeded, ct);

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
    private sealed record ConflictBody(string Reason, Guid InFlightJobId, string CurrentPhase);
    private sealed record AcceptedBody(Guid JobId, Guid CloudId);
}
