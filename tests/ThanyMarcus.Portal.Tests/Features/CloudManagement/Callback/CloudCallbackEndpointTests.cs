using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.Callback;

public sealed class CloudCallbackEndpointTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private const string ValidToken64 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Body_cloud_id_mismatch_returns_400_cloud_id_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).CreateClient();

        var routeId = Guid.NewGuid();
        var bodyId = Guid.NewGuid();
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{routeId}/callback", UriKind.Relative),
            new CloudCallbackRequest(bodyId, ValidToken64, "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("cloud_id_mismatch");
    }

    [Fact]
    public async Task Enrollment_token_wrong_length_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).CreateClient();

        var id = Guid.NewGuid();
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{id}/callback", UriKind.Relative),
            new CloudCallbackRequest(id, "too-short", "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Empty_admin_token_returns_400_missing_cloud_admin_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).CreateClient();

        var id = Guid.NewGuid();
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{id}/callback", UriKind.Relative),
            new CloudCallbackRequest(id, ValidToken64, ""), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("missing_cloud_admin_token");
    }

    [Fact]
    public async Task Unknown_cloud_returns_404_cloud_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).CreateClient();

        var id = Guid.NewGuid();
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{id}/callback", UriKind.Relative),
            new CloudCallbackRequest(id, ValidToken64, "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("cloud_not_found");
    }

    [Fact]
    public async Task Cloud_without_create_job_returns_404_no_create_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await SeedCloudOnlyAsync(ct);
        using var client = Factory.WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, ValidToken64, "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("no_create_job");
    }

    [Fact]
    public async Task Wrong_token_same_length_returns_401_identical_to_length_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cloud, _) = await SeedCloudWithJobAsync(SagaStatus.AwaitingCloudCallback, ValidToken64, ct);
        using var client = Factory.WithClock(Clock).CreateClient();

        var wrongButValidLength =
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, wrongButValidLength, "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_state_returns_409_wrong_state_with_current_phase()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cloud, _) = await SeedCloudWithJobAsync(SagaStatus.TfPlanning, ValidToken64, ct);
        using var client = Factory.WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, ValidToken64, "admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<ConflictBody>(ct);
        body!.Error.ShouldBe("wrong_state");
        body.Current.ShouldBe(SagaStatus.TfPlanning);
    }

    [Fact]
    public async Task Success_path_transitions_to_awaiting_cert_and_fires_notify()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cloud, job) = await SeedCloudWithJobAsync(SagaStatus.AwaitingCloudCallback, ValidToken64, ct);

        var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listenConn = new NpgsqlConnection(Postgres.ConnectionString);
        await listenConn.OpenAsync(ct);
        listenConn.Notification += (_, e) =>
        {
            if (e.Channel == "provisioning_job_changed")
                notified.TrySetResult(e.Payload);
        };
        await using (var listenCmd = new NpgsqlCommand("LISTEN provisioning_job_changed", listenConn))
            await listenCmd.ExecuteNonQueryAsync(ct);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var waitTask = Task.Run(async () =>
        {
            try { await listenConn.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { }
        }, linked.Token);

        using var client = Factory.WithClock(Clock).CreateClient();
        var res = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, ValidToken64, "the-admin-token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ok = await res.Content.ReadFromJsonAsync<OkPayload>(ct);
        ok!.Ok.ShouldBe(true);
        ok.Idempotent.ShouldBeNull();

        await waitTask;
        notified.Task.IsCompletedSuccessfully.ShouldBeTrue(
            "expected pg_notify('provisioning_job_changed', ...) within 2s");
        var payload = await notified.Task;
        payload.ShouldBe(job.Id.ToString());

        Db.ChangeTracker.Clear();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.AwaitingCert);
        reloadedJob.EventsLog.RootElement.GetRawText().ShouldContain("cloud_registered");

        reloadedJob.AdminTokenCiphertext.ShouldNotBeNull();
        var dpp = Factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
        var plaintext = Encoding.UTF8.GetString(protector.Unprotect(reloadedJob.AdminTokenCiphertext!));
        plaintext.ShouldBe("the-admin-token");

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.AdminStartedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Idempotent_replay_returns_200_idempotent_without_double_writes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cloud, job) = await SeedCloudWithJobAsync(SagaStatus.AwaitingCloudCallback, ValidToken64, ct);
        using var client = Factory.WithClock(Clock).CreateClient();

        var first = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, ValidToken64, "the-admin-token"), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        Db.ChangeTracker.Clear();
        var afterFirst = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        var startedAtAfterFirst = afterFirst.AdminStartedAt;
        startedAtAfterFirst.ShouldNotBeNull();

        Clock.Advance(Duration.FromSeconds(30));

        var second = await client.PostAsJsonAsync(
            new Uri($"/api/clouds/{cloud.Id}/callback", UriKind.Relative),
            new CloudCallbackRequest(cloud.Id, ValidToken64, "the-admin-token"), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ok = await second.Content.ReadFromJsonAsync<OkPayload>(ct);
        ok!.Idempotent.ShouldBe(true);
        ok.Ok.ShouldBeNull();

        Db.ChangeTracker.Clear();
        var afterSecond = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        afterSecond.AdminStartedAt.ShouldBe(startedAtAfterFirst);
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.AwaitingCert);
    }

    private async Task<Cloud> SeedCloudOnlyAsync(CancellationToken ct)
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
            ProvisioningStatus = SagaStatus.AwaitingCloudCallback,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cloud;
    }

    private async Task<(Cloud cloud, ProvisioningJob job)> SeedCloudWithJobAsync(
        string status, string enrollmentToken, CancellationToken ct)
    {
        var cloud = await SeedCloudOnlyAsync(ct);
        var now = Clock.GetCurrentInstant();
        var job = new ProvisioningJob
        {
            CloudId = cloud.Id,
            UserId = cloud.UserId,
            Kind = SagaKinds.Create,
            Status = status,
            EnrollmentToken = enrollmentToken,
            Payload = JsonDocument.Parse("{}"),
            EventsLog = JsonDocument.Parse("[]"),
            NextVisibleAt = now,
            PhaseStartedAt = now,
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.ProvisioningJobs.Add(job);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (cloud, job);
    }

    private sealed record ErrorBody(string Error);
    private sealed record ConflictBody(string Error, string Current);
    private sealed record OkPayload(bool? Ok, bool? Idempotent);
}
