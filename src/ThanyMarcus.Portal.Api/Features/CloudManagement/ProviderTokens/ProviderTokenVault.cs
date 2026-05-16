using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public sealed class ProviderTokenVault(PortalDbContext db, IClock clock) : IProviderTokenVault
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    public async Task AddAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var existing = await db.EncryptedProviderTokens
            .AnyAsync(t => t.UserId == userId && t.Provider == provider, ct);
        if (existing) throw new ProviderTokenAlreadyExistsException(userId, provider);

        var (ciphertext, nonce, tag) = Encrypt(plaintextToken, dek.Span);
        var now = clock.GetCurrentInstant();

        db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId      = userId,
            Provider    = provider,
            Ciphertext  = ciphertext,
            Nonce       = nonce,
            Tag         = tag,
            CreatedAt   = now,
            UpdatedAt   = now,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var (ciphertext, nonce, tag) = Encrypt(plaintextToken, dek.Span);
        var existing = await db.EncryptedProviderTokens
            .SingleOrDefaultAsync(t => t.UserId == userId && t.Provider == provider, ct);
        var now = clock.GetCurrentInstant();

        if (existing is null)
        {
            db.EncryptedProviderTokens.Add(new EncryptedProviderToken
            {
                UserId      = userId,
                Provider    = provider,
                Ciphertext  = ciphertext,
                Nonce       = nonce,
                Tag         = tag,
                CreatedAt   = now,
                UpdatedAt   = now,
            });
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.Nonce      = nonce;
            existing.Tag        = tag;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<byte[]?> DecryptAsync(Guid userId, string provider, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var row = await db.EncryptedProviderTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.UserId == userId && t.Provider == provider, ct);
        if (row is null) return null;

        var plaintext = new byte[row.Ciphertext.Length];
        using var aes = new AesGcm(dek.Span, tagSizeInBytes: TagSize);
        aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plaintext);
        return plaintext;
    }

    public async Task RemoveAsync(Guid userId, string provider, CancellationToken ct)
    {
        await db.EncryptedProviderTokens
            .Where(t => t.UserId == userId && t.Provider == provider)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderTokenSummary>> ListAsync(Guid userId, CancellationToken ct)
    {
        return await db.EncryptedProviderTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Provider)
            .Select(t => new ProviderTokenSummary(t.Provider, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }

    private static (byte[] ciphertext, byte[] nonce, byte[] tag) Encrypt(string plaintextToken, ReadOnlySpan<byte> dek)
    {
        var plaintext  = Encoding.UTF8.GetBytes(plaintextToken);
        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var tag        = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        using var aes  = new AesGcm(dek, tagSizeInBytes: TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintext);
        return (ciphertext, nonce, tag);
    }
}
