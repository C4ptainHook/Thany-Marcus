using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class PassphraseService(PortalDbContext db, IClock clock)
{
    public static readonly Argon2Params DefaultParams = new(MemoryKiB: 47104, Iterations: 2, Parallelism: 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<bool> InitAsync(Guid userId, string passphrase, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is { Length: > 0 }) return false;

        var salt   = RandomNumberGenerator.GetBytes(16);
        var dek    = RandomNumberGenerator.GetBytes(32);
        var nonce  = RandomNumberGenerator.GetBytes(12);
        var tag    = new byte[16];
        var cipher = new byte[dek.Length];

        var kek = DeriveKek(passphrase, salt, DefaultParams);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Encrypt(nonce, dek, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(dek);
        }

        user.PassphraseArgon2Salt   = salt;
        user.PassphraseArgon2Params = JsonSerializer.SerializeToDocument(DefaultParams, JsonOpts);
        user.PassphraseWrappedDek   = cipher;
        user.PassphraseWrapNonce    = nonce;
        user.PassphraseWrapTag      = tag;
        user.PassphraseSetAt        = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UnlockResult> TryUnwrapDekAsync(Guid userId, string passphrase, byte[] dekBuffer, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is null or { Length: 0 }) return UnlockResult.Failed;

        var paramsObj = user.PassphraseArgon2Params!.Deserialize<Argon2Params>(JsonOpts)!;
        var kek = DeriveKek(passphrase, user.PassphraseArgon2Salt!, paramsObj);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Decrypt(user.PassphraseWrapNonce!, user.PassphraseWrappedDek!, user.PassphraseWrapTag!, dekBuffer);
            return UnlockResult.Unlocked;
        }
        catch (AuthenticationTagMismatchException)
        {
            return UnlockResult.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] DeriveKek(string passphrase, byte[] salt, Argon2Params p)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt                = salt,
            MemorySize          = p.MemoryKiB,
            Iterations          = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(32);
    }
}
