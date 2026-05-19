using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

public sealed class CloudSecretBundle(PortalDbContext db, IClock clock) : ICloudSecretBundle
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    public async Task PutAsync(Guid cloudId, string kind, string plaintext, ReadOnlyMemory<byte> dek,
        Instant? expiresAt, CancellationToken ct)
    {
        var (ciphertext, nonce, tag) = Encrypt(plaintext, dek.Span);
        var now = clock.GetCurrentInstant();

        var existing = await db.CloudSecrets
            .SingleOrDefaultAsync(s => s.CloudId == cloudId && s.Kind == kind, ct);

        if (existing is null)
        {
            db.CloudSecrets.Add(new CloudSecret
            {
                CloudId    = cloudId,
                Kind       = kind,
                Ciphertext = ciphertext,
                Nonce      = nonce,
                Tag        = tag,
                ExpiresAt  = expiresAt,
                CreatedAt  = now,
                UpdatedAt  = now,
            });
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.Nonce      = nonce;
            existing.Tag        = tag;
            existing.ExpiresAt  = expiresAt;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> GetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var plaintext = await TryGetAsync(cloudId, kind, dek, ct);
        if (plaintext is null) throw new CloudSecretNotFoundException(cloudId, kind);
        return plaintext;
    }

    public async Task<string?> TryGetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var row = await db.CloudSecrets
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.CloudId == cloudId && s.Kind == kind, ct);
        if (row is null) return null;

        var plaintext = new byte[row.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek.Span, tagSizeInBytes: TagSize);
            aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<Instant?> GetExpiresAtAsync(Guid cloudId, string kind, CancellationToken ct)
    {
        return await db.CloudSecrets
            .AsNoTracking()
            .Where(s => s.CloudId == cloudId && s.Kind == kind)
            .Select(s => s.ExpiresAt)
            .SingleOrDefaultAsync(ct);
    }

    public async Task DeleteAsync(Guid cloudId, string kind, CancellationToken ct)
    {
        await db.CloudSecrets
            .Where(s => s.CloudId == cloudId && s.Kind == kind)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAllForCloudAsync(Guid cloudId, CancellationToken ct)
    {
        await db.CloudSecrets
            .Where(s => s.CloudId == cloudId)
            .ExecuteDeleteAsync(ct);
    }

    private static (byte[] ciphertext, byte[] nonce, byte[] tag) Encrypt(string plaintext, ReadOnlySpan<byte> dek)
    {
        var bytes      = Encoding.UTF8.GetBytes(plaintext);
        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var tag        = new byte[TagSize];
        var ciphertext = new byte[bytes.Length];
        using var aes  = new AesGcm(dek, tagSizeInBytes: TagSize);
        aes.Encrypt(nonce, bytes, ciphertext, tag);
        CryptographicOperations.ZeroMemory(bytes);
        return (ciphertext, nonce, tag);
    }
}
