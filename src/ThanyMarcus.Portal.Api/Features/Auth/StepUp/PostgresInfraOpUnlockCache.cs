using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class PostgresInfraOpUnlockCache(
    PortalDbContext db,
    IDataProtectionProvider dp,
    IClock clock) : IInfraOpUnlockCache
{
    public static readonly Duration SlidingTtl = Duration.FromMinutes(60);
    private readonly IDataProtector _protector = dp.CreateProtector("step-up-unlock.v1");

    public async Task<bool> TryGetAsync(Guid userId, byte[] dekDestination, CancellationToken ct)
    {
        var entry = await db.StepUpUnlocks.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (entry is null) return false;

        var now = clock.GetCurrentInstant();
        if (entry.ExpiresAt <= now)
        {
            db.StepUpUnlocks.Remove(entry);
            await db.SaveChangesAsync(ct);
            return false;
        }

        byte[] plaintext;
        try { plaintext = _protector.Unprotect(entry.EncryptedDek); }
        catch (CryptographicException) { return false; }

        try
        {
            if (plaintext.Length != dekDestination.Length)
                return false;

            plaintext.AsSpan().CopyTo(dekDestination);
            entry.LastUsedAt = now;
            entry.ExpiresAt = now + SlidingTtl;
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SetAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var encrypted = _protector.Protect(dek.ToArray());
        var now = clock.GetCurrentInstant();
        var existing = await db.StepUpUnlocks.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (existing is null)
        {
            db.StepUpUnlocks.Add(new StepUpUnlock
            {
                UserId = userId,
                EncryptedDek = encrypted,
                ExpiresAt = now + SlidingTtl,
                LastUsedAt = now,
                CreatedAt = now,
            });
        }
        else
        {
            existing.EncryptedDek = encrypted;
            existing.ExpiresAt = now + SlidingTtl;
            existing.LastUsedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        await db.StepUpUnlocks.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);
    }
}
