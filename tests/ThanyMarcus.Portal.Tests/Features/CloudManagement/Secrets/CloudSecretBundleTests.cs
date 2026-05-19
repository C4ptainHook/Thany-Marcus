using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.Secrets;

public sealed class CloudSecretBundleTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private static byte[] RandomDek() => RandomNumberGenerator.GetBytes(32);

    private async Task<Cloud> InsertCloudAsync()
    {
        var ct = TestContext.Current.CancellationToken;
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
            Name = "c",
            Provider = "digitalocean",
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = SagaStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cloud;
    }

    [Fact]
    public async Task Put_then_get_round_trips_plaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await InsertCloudAsync();
        var bundle = new CloudSecretBundle(Db, Clock);
        var dek = RandomDek();
        var expires = Clock.GetCurrentInstant().Plus(Duration.FromDays(30));

        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, "access-xyz", dek, expires, ct);

        var got = await bundle.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, dek, ct);
        got.ShouldBe("access-xyz");

        var storedExpires = await bundle.GetExpiresAtAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, ct);
        storedExpires.ShouldBe(expires);
    }

    [Fact]
    public async Task TryGet_returns_null_when_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await InsertCloudAsync();
        var bundle = new CloudSecretBundle(Db, Clock);

        (await bundle.TryGetAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, RandomDek(), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Decrypt_with_wrong_dek_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await InsertCloudAsync();
        var bundle = new CloudSecretBundle(Db, Clock);
        var realDek  = RandomDek();
        var wrongDek = RandomDek();

        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, "secret", realDek, null, ct);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            async () => await bundle.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, wrongDek, ct));
    }

    [Fact]
    public async Task Put_overwrites_same_kind_for_same_cloud()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await InsertCloudAsync();
        var bundle = new CloudSecretBundle(Db, Clock);
        var dek = RandomDek();

        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, "first",  dek, null, ct);
        Db.ChangeTracker.Clear();
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, "second", dek, null, ct);
        Db.ChangeTracker.Clear();

        var count = await Db.CloudSecrets
            .CountAsync(s => s.CloudId == cloud.Id && s.Kind == CloudSecretKind.DoOAuthAccess, ct);
        count.ShouldBe(1);
        (await bundle.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, dek, ct)).ShouldBe("second");
    }

    [Fact]
    public async Task DeleteAllForCloud_removes_every_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = await InsertCloudAsync();
        var bundle = new CloudSecretBundle(Db, Clock);
        var dek = RandomDek();

        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,    "a", dek, null, ct);
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh,   "r", dek, null, ct);
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, "k", dek, null, ct);
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   "s", dek, null, ct);

        await bundle.DeleteAllForCloudAsync(cloud.Id, ct);

        (await Db.CloudSecrets.CountAsync(s => s.CloudId == cloud.Id, ct)).ShouldBe(0);
    }
}
