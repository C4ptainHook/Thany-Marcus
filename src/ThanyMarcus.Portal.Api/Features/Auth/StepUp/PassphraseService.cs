using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class PassphraseService(PortalDbContext db, IClock clock)
{
    public async Task<bool> InitAsync(Guid userId, string passphrase, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is { Length: > 0 }) return false;

        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            WriteWrap(user, dek, passphrase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        user.PassphraseSetAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Replace the passphrase wrap for a DEK recovered through a reset path (Emergency Kit or
    // TOTP). The DEK itself is unchanged — only the key it is sealed under is.
    public async Task RewrapAsync(Guid userId, byte[] dek, string newPassphrase, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        WriteWrap(user, dek, newPassphrase);
        user.PassphraseSetAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task<UnlockResult> TryUnwrapDekAsync(Guid userId, string passphrase, byte[] dekBuffer, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is null or { Length: 0 }) return UnlockResult.Failed;

        return DekEnvelope.TryUnwrap(
            user.PassphraseArgon2Salt!, user.PassphraseArgon2Params!,
            user.PassphraseWrappedDek!, user.PassphraseWrapNonce!, user.PassphraseWrapTag!,
            passphrase, dekBuffer)
            ? UnlockResult.Unlocked
            : UnlockResult.Failed;
    }

    private static void WriteWrap(User user, byte[] dek, string passphrase)
    {
        var w = DekEnvelope.Wrap(dek, passphrase);
        user.PassphraseArgon2Salt   = w.Salt;
        user.PassphraseArgon2Params = w.Params;
        user.PassphraseWrappedDek   = w.Cipher;
        user.PassphraseWrapNonce    = w.Nonce;
        user.PassphraseWrapTag      = w.Tag;
    }
}
