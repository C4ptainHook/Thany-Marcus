using Microsoft.EntityFrameworkCore;
using NodaTime;
using QRCoder;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class EmergencyKitService(PortalDbContext db, IClock clock)
{
    // Mint a fresh kit for an unlocked user. The plaintext recovery string is returned exactly
    // once; the server keeps only its Argon2id hash and the DEK sealed under it.
    public async Task<string> GenerateAsync(Guid userId, byte[] dek, CancellationToken ct)
    {
        var phrase = WordList.Generate();
        var normalized = WordList.Normalize(phrase);
        var wrapped = DekEnvelope.Wrap(dek, normalized);
        var now = clock.GetCurrentInstant();

        await db.EmergencyKits
            .Where(k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct);

        db.EmergencyKits.Add(new EmergencyKit
        {
            UserId = userId,
            HashedString = Argon2Phc.Hash(normalized),
            WrapArgon2Salt = wrapped.Salt,
            WrapArgon2Params = wrapped.Params,
            WrappedDek = wrapped.Cipher,
            WrapNonce = wrapped.Nonce,
            WrapTag = wrapped.Tag,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return phrase;
    }

    // Verify a typed recovery string against the active kit and, on success, unwrap the DEK into
    // destination. Returns the tracked kit so the caller can mark it used; null on any mismatch.
    public async Task<EmergencyKit?> TryUnlockAsync(
        Guid userId, string recoveryString, byte[] destination, CancellationToken ct)
    {
        var normalized = WordList.Normalize(recoveryString);
        var kit = await db.EmergencyKits.SingleOrDefaultAsync(
            k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null, ct);
        if (kit is null) return null;
        if (!Argon2Phc.Verify(kit.HashedString, normalized)) return null;
        if (!DekEnvelope.TryUnwrap(
                kit.WrapArgon2Salt, kit.WrapArgon2Params, kit.WrappedDek, kit.WrapNonce, kit.WrapTag,
                normalized, destination))
            return null;
        return kit;
    }

    public Task<EmergencyKit?> GetActiveAsync(Guid userId, CancellationToken ct) =>
        db.EmergencyKits.AsNoTracking().SingleOrDefaultAsync(
            k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null, ct);

    public static string BuildQrPngDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
