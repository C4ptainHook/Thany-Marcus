using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using OtpNet;
using QRCoder;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public sealed class TotpService(IDataProtectionProvider dp, IClock clock)
{
    private readonly IDataProtector _protector = dp.CreateProtector("totp-secrets.v1");

    public string GenerateSecret()
    {
        var bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);
        return Base32Encoding.ToString(bytes);
    }

    public string BuildQrPngDataUri(string secret, string email, string issuer = "Thany-Marcus")
    {
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}"
                + $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
        using var qr = new QRCodeGenerator();
        using var data = qr.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public bool Verify(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
        byte[] bytes;
        try { bytes = Base32Encoding.ToBytes(secret); }
        catch { return false; }
        var totp = new OtpNet.Totp(bytes);
        return totp.VerifyTotp(
            clock.GetCurrentInstant().ToDateTimeUtc(),
            code,
            out _,
            VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    // Data-protection envelope, not AES-GCM: the three-column shape from ADR-0029 is reused, but
    // Nonce/Tag stay empty because IDataProtector returns a single opaque blob.
    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(string secret)
    {
        var blob = _protector.Protect(Encoding.UTF8.GetBytes(secret));
        return (blob, Array.Empty<byte>(), Array.Empty<byte>());
    }

    public string Decrypt(byte[] ciphertext) =>
        Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));

    public async Task<TotpChallengeResult> VerifyChallengeAsync(
        PortalDbContext db, Guid userId, string code, CancellationToken ct)
    {
        var row = await db.TotpSecrets
            .SingleOrDefaultAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
        if (row is null) return TotpChallengeResult.Failed;
        var secret = Decrypt(row.Ciphertext);
        return Verify(secret, code) ? TotpChallengeResult.Verified : TotpChallengeResult.Failed;
    }
}
