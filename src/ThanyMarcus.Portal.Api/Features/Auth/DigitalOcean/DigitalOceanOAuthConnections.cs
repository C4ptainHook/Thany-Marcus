using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed class DigitalOceanOAuthConnections(PortalDbContext db, IClock clock) : IDigitalOceanOAuthConnections
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    public async Task SaveAsync(Guid userId, string accessToken, string refreshToken, Instant accessExpiresAt,
                                ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var (accCipher, accNonce, accTag)  = Encrypt(accessToken,  dek.Span);
        var (refCipher, refNonce, refTag) = Encrypt(refreshToken, dek.Span);
        var now = clock.GetCurrentInstant();

        var existing = await db.DigitalOceanOAuthConnections.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (existing is null)
        {
            db.DigitalOceanOAuthConnections.Add(new DigitalOceanOAuthConnection
            {
                UserId             = userId,
                AccessCiphertext   = accCipher,
                AccessNonce        = accNonce,
                AccessTag          = accTag,
                AccessExpiresAt    = accessExpiresAt,
                RefreshCiphertext  = refCipher,
                RefreshNonce       = refNonce,
                RefreshTag         = refTag,
                ConnectionStatus   = DigitalOceanConnectionStatus.Connected,
                CreatedAt          = now,
                UpdatedAt          = now,
            });
        }
        else
        {
            existing.AccessCiphertext  = accCipher;
            existing.AccessNonce       = accNonce;
            existing.AccessTag         = accTag;
            existing.AccessExpiresAt   = accessExpiresAt;
            existing.RefreshCiphertext = refCipher;
            existing.RefreshNonce      = refNonce;
            existing.RefreshTag        = refTag;
            existing.ConnectionStatus  = DigitalOceanConnectionStatus.Connected;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsConnectedAsync(Guid userId, CancellationToken ct) =>
        await db.DigitalOceanOAuthConnections.AsNoTracking().AnyAsync(c => c.UserId == userId, ct);

    public async Task<DigitalOceanConnectionInfo?> GetInfoAsync(Guid userId, CancellationToken ct)
    {
        return await db.DigitalOceanOAuthConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new DigitalOceanConnectionInfo(c.AccessExpiresAt, c.ConnectionStatus))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<string?> GetAccessTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var row = await db.DigitalOceanOAuthConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.AccessCiphertext, c.AccessNonce, c.AccessTag })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        return Decrypt(row.AccessCiphertext, row.AccessNonce, row.AccessTag, dek.Span);
    }

    public async Task<string?> GetRefreshTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var row = await db.DigitalOceanOAuthConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.RefreshCiphertext, c.RefreshNonce, c.RefreshTag })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        return Decrypt(row.RefreshCiphertext, row.RefreshNonce, row.RefreshTag, dek.Span);
    }

    public async Task SetConnectionStatusAsync(Guid userId, string status, CancellationToken ct)
    {
        await db.DigitalOceanOAuthConnections
            .Where(c => c.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConnectionStatus, status), ct);
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken ct)
    {
        await db.DigitalOceanOAuthConnections
            .Where(c => c.UserId == userId)
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

    private static string Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, ReadOnlySpan<byte> dek)
    {
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek, tagSizeInBytes: TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
