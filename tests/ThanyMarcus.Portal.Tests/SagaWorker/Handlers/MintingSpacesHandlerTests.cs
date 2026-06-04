using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class MintingSpacesHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task DigitalOcean_mints_spaces_key_and_transitions_to_tf_planning()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.MintingSpaces,
            provider: "digitalocean",
            ct: ct);

        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        var dek = SagaTestSeed.MakeDek();
        await connections.SaveAsync(user.Id, "do-access-token", "do-refresh-token",
            Clock.GetCurrentInstant().Plus(Duration.FromDays(30)), dek, ct);
        Db.ChangeTracker.Clear();

        var fakeDo = new FakeDigitalOceanOAuthClient
        {
            OnMint = (_, _) => new DoSpacesKeyMint { AccessKeyId = "AK123", SecretKey = "S3CR3T" },
        };
        var handler = new MintingSpacesHandler(
            Db, Clock,
            TestSagaCredentials.Source(Db, dp, Clock),
            TestProvisioningProviders.Registry(Db, Clock, fakeDo),
            new CloudSecretBundle(Db, Clock),
            new DigitalOceanOAuthConnections(Db, Clock),
            fakeDo,
            new InstantSpacesKeyProbe(),
            NullLogger<MintingSpacesHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var jobAfter = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        jobAfter.Status.ShouldBe(SagaStatus.TfPlanning);

        var cloudAfter = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        cloudAfter.ProvisioningStatus.ShouldBe(SagaStatus.TfPlanning);
        cloudAfter.MintingSpacesStartedAt.ShouldNotBeNull();

        var bundleAfter = new CloudSecretBundle(Db, Clock);
        (await bundleAfter.GetAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, dek, ct)).ShouldBe("AK123");
        (await bundleAfter.GetAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   dek, ct)).ShouldBe("S3CR3T");

        fakeDo.MintCalls.Count.ShouldBe(1);
        fakeDo.MintCalls[0].AccessToken.ShouldBe("do-access-token");
    }

    [Fact]
    public async Task NonDigitalOcean_provider_skips_minting_and_advances_to_tf_planning()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.MintingSpaces,
            provider: "stub",
            ct: ct);
        Db.ChangeTracker.Clear();

        var fakeDo = new FakeDigitalOceanOAuthClient();
        var handler = new MintingSpacesHandler(
            Db, Clock,
            TestSagaCredentials.Source(Db, dp, Clock),
            TestProvisioningProviders.Registry(Db, Clock, fakeDo),
            new CloudSecretBundle(Db, Clock),
            new DigitalOceanOAuthConnections(Db, Clock),
            fakeDo,
            new InstantSpacesKeyProbe(),
            NullLogger<MintingSpacesHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var jobAfter = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        jobAfter.Status.ShouldBe(SagaStatus.TfPlanning);
        fakeDo.MintCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DigitalOcean_mint_failure_transitions_to_failed_minting_spaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.MintingSpaces,
            provider: "digitalocean",
            ct: ct);

        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        var dek = SagaTestSeed.MakeDek();
        await connections.SaveAsync(user.Id, "expired-token", "refresh-token",
            Clock.GetCurrentInstant(), dek, ct);
        Db.ChangeTracker.Clear();

        var fakeDo = new FakeDigitalOceanOAuthClient
        {
            OnMint = (_, _) => throw new DigitalOceanOAuthException("revoked", 401),
        };
        var handler = new MintingSpacesHandler(
            Db, Clock,
            TestSagaCredentials.Source(Db, dp, Clock),
            TestProvisioningProviders.Registry(Db, Clock, fakeDo),
            new CloudSecretBundle(Db, Clock),
            new DigitalOceanOAuthConnections(Db, Clock),
            fakeDo,
            new InstantSpacesKeyProbe(),
            NullLogger<MintingSpacesHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var jobAfter = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        jobAfter.Status.ShouldBe(SagaStatus.FailedMintingSpaces);
    }

    [Fact]
    public async Task DigitalOcean_missing_user_connection_transitions_to_failed_minting_spaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.MintingSpaces,
            provider: "digitalocean",
            ct: ct);
        Db.ChangeTracker.Clear();

        var handler = new MintingSpacesHandler(
            Db, Clock,
            TestSagaCredentials.Source(Db, dp, Clock),
            TestProvisioningProviders.Registry(Db, Clock),
            new CloudSecretBundle(Db, Clock),
            new DigitalOceanOAuthConnections(Db, Clock),
            new FakeDigitalOceanOAuthClient(),
            new InstantSpacesKeyProbe(),
            NullLogger<MintingSpacesHandler>.Instance);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var jobAfter = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        jobAfter.Status.ShouldBe(SagaStatus.FailedMintingSpaces);
    }
}
