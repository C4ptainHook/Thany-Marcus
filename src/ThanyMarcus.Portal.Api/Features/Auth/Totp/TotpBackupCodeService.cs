using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public sealed class TotpBackupCodeService(PortalDbContext db, IClock clock)
{
    private const int CodeCount = 8;
    private const int CodeLength = 8;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public async Task<IReadOnlyList<string>> IssueAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.TotpBackupCodes.Where(b => b.UserId == userId).ToListAsync(ct);
        db.TotpBackupCodes.RemoveRange(existing);

        var now = clock.GetCurrentInstant();
        var plaintexts = new List<string>(CodeCount);
        var rows = new List<TotpBackupCode>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
        {
            var code = GenerateCode();
            plaintexts.Add(code);
            rows.Add(new TotpBackupCode
            {
                UserId = userId,
                HashedCode = HashCode(code),
                CreatedAt = now,
            });
        }
        db.TotpBackupCodes.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return plaintexts;
    }

    public async Task<bool> RedeemAsync(Guid userId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var rows = await db.TotpBackupCodes
            .Where(b => b.UserId == userId && b.UsedAt == null)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (Argon2idVerify(row.HashedCode, code))
            {
                row.UsedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(ct);
                return true;
            }
        }
        return false;
    }

    public async Task PurgeUnusedAsync(Guid userId, CancellationToken ct)
    {
        await db.TotpBackupCodes
            .Where(b => b.UserId == userId && b.UsedAt == null)
            .ExecuteDeleteAsync(ct);
    }

    private static string GenerateCode()
    {
        Span<byte> buf = stackalloc byte[CodeLength];
        Span<char> chars = stackalloc char[CodeLength];
        RandomNumberGenerator.Fill(buf);
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[buf[i] & 0x1F];
        return new string(chars);
    }

    private static string HashCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = ComputeArgon2id(code, salt);
        return $"$argon2id$v=19$m=19456,t=2,p=2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Argon2idVerify(string phc, string code)
    {
        var parts = phc.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expected = Convert.FromBase64String(parts[5]);
        }
        catch
        {
            return false;
        }
        var actual = ComputeArgon2id(code, salt, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] ComputeArgon2id(string code, byte[] salt, int hashLength = 32)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(code))
        {
            DegreeOfParallelism = 2,
            MemorySize = 19456,
            Iterations = 2,
            Salt = salt,
        };
        return argon.GetBytes(hashLength);
    }
}
