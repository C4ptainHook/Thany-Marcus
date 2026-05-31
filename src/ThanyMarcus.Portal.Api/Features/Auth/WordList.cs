using System.Security.Cryptography;

namespace ThanyMarcus.Portal.Api.Features.Auth;

// Recovery strings are 8 words drawn from the 2048-word BIP39 English list:
// 8 * 11 bits = 88 bits of entropy, packed big-endian into 11 bytes.
public static class WordList
{
    public const int WordCount = 8;
    private const int BitsPerWord = 11;            // 2048 == 1 << 11
    private const int EntropyBytes = WordCount * BitsPerWord / 8;   // 11

    private static readonly string[] Words = EmbeddedLines.Read(typeof(WordList).Assembly, "bip39-english.txt");
    private static readonly Dictionary<string, int> Index = BuildIndex();

    private static Dictionary<string, int> BuildIndex()
    {
        if (Words.Length != 1 << BitsPerWord)
            throw new InvalidOperationException($"Expected {1 << BitsPerWord} words, found {Words.Length}.");
        var map = new Dictionary<string, int>(Words.Length, StringComparer.Ordinal);
        for (var i = 0; i < Words.Length; i++) map[Words[i]] = i;
        return map;
    }

    public static string Generate()
    {
        Span<byte> entropy = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        return Pack(entropy);
    }

    public static string Pack(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != EntropyBytes)
            throw new ArgumentException($"Expected {EntropyBytes} bytes.", nameof(entropy));

        var words = new string[WordCount];
        for (var w = 0; w < WordCount; w++)
            words[w] = Words[ReadBits(entropy, w * BitsPerWord)];
        return string.Join(' ', words);
    }

    public static bool TryUnpack(string phrase, out byte[] entropy)
    {
        entropy = new byte[EntropyBytes];
        var tokens = Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != WordCount) return false;

        for (var w = 0; w < WordCount; w++)
        {
            if (!Index.TryGetValue(tokens[w], out var idx)) return false;
            WriteBits(entropy, w * BitsPerWord, idx);
        }
        return true;
    }

    public static string Normalize(string phrase) =>
        string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static int ReadBits(ReadOnlySpan<byte> bytes, int bitOffset)
    {
        var value = 0;
        for (var i = 0; i < BitsPerWord; i++)
        {
            var bit = bitOffset + i;
            var set = (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;
            value = (value << 1) | set;
        }
        return value;
    }

    private static void WriteBits(Span<byte> bytes, int bitOffset, int value)
    {
        for (var i = 0; i < BitsPerWord; i++)
        {
            var bit = bitOffset + i;
            var set = (value >> (BitsPerWord - 1 - i)) & 1;
            if (set != 0) bytes[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
    }
}
