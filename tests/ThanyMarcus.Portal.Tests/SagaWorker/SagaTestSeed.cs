using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

internal static class SagaTestSeed
{
    public static async Task<(User user, Cloud cloud, ProvisioningJob job)> SeedAsync(
        PortalDbContext db,
        IClock clock,
        IDataProtectionProvider dp,
        string status = SagaStatus.TfPlanning,
        string provider = "stub",
        bool seedUnlock = true,
        bool seedProviderToken = false,
        bool seedCloudflareToken = false,
        string kind = SagaKinds.Create,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"g-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            Name = "Test",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(user);

        var cloud = new Cloud
        {
            UserId = user.Id,
            Name = "test",
            Provider = provider,
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Clouds.Add(cloud);

        var job = new ProvisioningJob
        {
            CloudId = cloud.Id,
            UserId = user.Id,
            Kind = kind,
            Payload = JsonDocument.Parse("{}"),
            Status = status,
            NextVisibleAt = now,
            AttemptCount = 1,
            PhaseStartedAt = now,
            EventsLog = JsonDocument.Parse("[]"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProvisioningJobs.Add(job);

        await db.SaveChangesAsync(ct);

        if (seedUnlock)
        {
            var cache = new PostgresInfraOpUnlockCache(db, dp, clock);
            await cache.SetAsync(user.Id, MakeDek(), ct);
        }

        if (seedProviderToken)
        {
            var vault = new ProviderTokenVault(db, clock);
            await vault.AddAsync(user.Id, provider, "fake-token", MakeDek(), ct);
        }
        if (seedCloudflareToken)
        {
            var vault = new ProviderTokenVault(db, clock);
            await vault.AddAsync(user.Id, KnownProviders.Cloudflare, "fake-cf-token", MakeDek(), ct);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        return (user, cloud, job);
    }

    public static byte[] MakeDek(byte fill = 0x42)
    {
        var dek = new byte[32];
        Array.Fill(dek, fill);
        return dek;
    }
}
