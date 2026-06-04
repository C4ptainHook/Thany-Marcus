using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public sealed class PostgresSagaCredentialGrantStore(
    PortalDbContext db,
    IDataProtectionProvider dp,
    IClock clock) : ISagaCredentialGrantStore
{
    private readonly IDataProtector _protector = dp.CreateProtector("saga-credential-grant.v1");

    public async Task PutAsync(Guid cloudId, ReadOnlyMemory<byte> dek, Instant expiresAt, CancellationToken ct)
    {
        var sealedDek = _protector.Protect(dek.ToArray());
        var now = clock.GetCurrentInstant();
        var existing = await db.SagaCredentialGrants.SingleOrDefaultAsync(g => g.CloudId == cloudId, ct);
        if (existing is null)
        {
            db.SagaCredentialGrants.Add(new SagaCredentialGrant
            {
                CloudId   = cloudId,
                SealedDek = sealedDek,
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });
        }
        else
        {
            existing.SealedDek = sealedDek;
            existing.ExpiresAt = expiresAt;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryGetAsync(Guid cloudId, byte[] dekDestination, CancellationToken ct)
    {
        var entry = await db.SagaCredentialGrants.SingleOrDefaultAsync(g => g.CloudId == cloudId, ct);
        if (entry is null) return false;

        if (entry.ExpiresAt <= clock.GetCurrentInstant())
        {
            db.SagaCredentialGrants.Remove(entry);
            await db.SaveChangesAsync(ct);
            return false;
        }

        byte[] plaintext;
        try { plaintext = _protector.Unprotect(entry.SealedDek); }
        catch (CryptographicException) { return false; }

        try
        {
            if (plaintext.Length != dekDestination.Length)
                return false;

            plaintext.AsSpan().CopyTo(dekDestination);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task DeleteAsync(Guid cloudId, CancellationToken ct)
    {
        await db.SagaCredentialGrants.Where(g => g.CloudId == cloudId).ExecuteDeleteAsync(ct);
    }
}
