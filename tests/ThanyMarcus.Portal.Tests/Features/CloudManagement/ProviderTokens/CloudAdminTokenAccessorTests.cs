using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
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
    public async Task Returns_null_when_no_encrypted_column()
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

        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        var corrupted = trackedCloud.EncryptedCloudAdminToken!.ToArray();
        corrupted[0] ^= 0xFF;
        trackedCloud.EncryptedCloudAdminToken = corrupted;
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
        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
        trackedCloud.EncryptedCloudAdminToken = protector.Protect(Encoding.UTF8.GetBytes(plaintext));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cloud;
    }
}
