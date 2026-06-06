using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.ProviderTokens;

public sealed class CloudAdminTokenAccessorTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Round_trips_plaintext_via_data_protection()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var cloud = await SeedCloudWithEncryptedTokenAsync(dpp, "tm-admin-1234", ct);

        var accessor = new CloudAdminTokenAccessor(Db, dpp);
        var plaintext = await accessor.GetPlaintextAsync(cloud.Id, ct);

        plaintext.ShouldBe("tm-admin-1234");
    }

    [Fact]
    public async Task Returns_null_when_no_ephemeral_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var cloud = await SeedCloudOnlyAsync(ct);

        var accessor = new CloudAdminTokenAccessor(Db, dpp);
        var plaintext = await accessor.GetPlaintextAsync(cloud.Id, ct);

        plaintext.ShouldBeNull();
    }

    [Fact]
    public async Task Throws_on_tampered_ciphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var cloud = await SeedCloudWithEncryptedTokenAsync(dpp, "x", ct);

        var trackedJob = await Db.ProvisioningJobs.SingleAsync(j => j.CloudId == cloud.Id, ct);
        var corrupted = trackedJob.AdminTokenCiphertext!.ToArray();
        corrupted[0] ^= 0xFF;
        trackedJob.AdminTokenCiphertext = corrupted;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var accessor = new CloudAdminTokenAccessor(Db, dpp);
        await Should.ThrowAsync<Exception>(() => accessor.GetPlaintextAsync(cloud.Id, ct));
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
            ProvisioningStatus = "succeeded",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cloud;
    }

    private async Task<Cloud> SeedCloudWithEncryptedTokenAsync(
        EphemeralDataProtectionProvider dpp, string plaintext, CancellationToken ct)
    {
        var cloud = await SeedCloudOnlyAsync(ct);
        var now = Clock.GetCurrentInstant();
        var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
        Db.ProvisioningJobs.Add(new ProvisioningJob
        {
            CloudId = cloud.Id,
            UserId = cloud.UserId,
            Kind = SagaKinds.Create,
            Status = SagaStatus.AwaitingCert,
            Payload = JsonDocument.Parse("{}"),
            EventsLog = JsonDocument.Parse("[]"),
            NextVisibleAt = now,
            AdminTokenCiphertext = protector.Protect(Encoding.UTF8.GetBytes(plaintext)),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cloud;
    }
}
