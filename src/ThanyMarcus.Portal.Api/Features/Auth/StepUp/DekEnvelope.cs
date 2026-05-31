using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public readonly record struct WrappedDek(
    byte[] Salt, JsonDocument Params, byte[] Cipher, byte[] Nonce, byte[] Tag);

// The single DEK-wrapping scheme used by both the passphrase wrap and the Emergency Kit:
// derive a KEK from a human secret via Argon2id, then seal the DEK under it with AES-GCM.
public static class DekEnvelope
{
    public static readonly Argon2Params DefaultParams = new(MemoryKiB: 47104, Iterations: 2, Parallelism: 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static WrappedDek Wrap(ReadOnlySpan<byte> dek, string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[dek.Length];

        var kek = DeriveKek(secret, salt, DefaultParams);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Encrypt(nonce, dek, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return new WrappedDek(salt, JsonSerializer.SerializeToDocument(DefaultParams, JsonOpts), cipher, nonce, tag);
    }

    public static bool TryUnwrap(
        byte[] salt, JsonDocument prms, byte[] cipher, byte[] nonce, byte[] tag,
        string secret, byte[] destination)
    {
        var p = prms.Deserialize<Argon2Params>(JsonOpts)!;
        var kek = DeriveKek(secret, salt, p);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Decrypt(nonce, cipher, tag, destination);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] DeriveKek(string secret, byte[] salt, Argon2Params p)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt                = salt,
            MemorySize          = p.MemoryKiB,
            Iterations          = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(32);
    }
}
