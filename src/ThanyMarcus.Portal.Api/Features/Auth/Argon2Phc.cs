using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ThanyMarcus.Portal.Api.Features.Auth;

// Argon2id hashing in PHC string format, for verifying a typed secret against a stored hash.
public static class Argon2Phc
{
    private const int Memory = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 2;

    public static string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(value, salt, 32);
        return $"$argon2id$v=19$m={Memory},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string phc, string value)
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
        var actual = Compute(value, salt, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Compute(string value, byte[] salt, int hashLength)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(value))
        {
            DegreeOfParallelism = Parallelism,
            MemorySize = Memory,
            Iterations = Iterations,
            Salt = salt,
        };
        return argon.GetBytes(hashLength);
    }
}
