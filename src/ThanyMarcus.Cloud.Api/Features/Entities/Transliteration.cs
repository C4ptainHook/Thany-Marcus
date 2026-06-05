using System.Text;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

// Deterministic Ukrainian Cyrillic -> Latin romanization (Ukrainian National 2010 system) plus a
// small set of common non-systematic variants, used to seed cross-language aliases for person/place
// entities. Deterministic and explainable on purpose: catches exact transliterated names that a
// cross-lingual embedding may still miss, without an LLM dedup pass. Latin -> Cyrillic is lossy and
// ambiguous, so it is intentionally not attempted; the embedding + gray-zone merge flow covers the
// Latin-first case.
public static class Transliteration
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['д'] = "d", ['е'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
        ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f",
        ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ь'] = "", ['ґ'] = "g",
        // Iotated and й carry a word-initial form handled separately; these are the mid-word forms.
        ['є'] = "ie", ['ї'] = "i", ['й'] = "i", ['ю'] = "iu", ['я'] = "ia",
    };

    private static readonly Dictionary<char, string> InitialMap = new()
    {
        ['є'] = "ye", ['ї'] = "yi", ['й'] = "y", ['ю'] = "yu", ['я'] = "ya",
    };

    // Common non-systematic spellings the National 2010 rules don't produce (often Russian-derived
    // or historical). Keyed by the lowercased Cyrillic form.
    private static readonly Dictionary<string, string[]> Exceptions = new(StringComparer.Ordinal)
    {
        ["київ"] = ["Kiev"],
        ["львів"] = ["Lvov"],
        ["харків"] = ["Kharkov"],
        ["одеса"] = ["Odessa"],
        ["дніпро"] = ["Dnieper", "Dnipropetrovsk"],
        ["чорнобиль"] = ["Chernobyl"],
        ["миколаїв"] = ["Nikolaev"],
    };

    public static bool ContainsCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (Map.ContainsKey(lower) || InitialMap.ContainsKey(lower) || lower is 'г' or '’' or '\'' or '`') return true;
        }
        return false;
    }

    // Latin spelling variants for a Cyrillic name. Returns empty when the input has no Cyrillic
    // (nothing to romanize) or when every produced form equals the input.
    public static IReadOnlyList<string> CyrillicToLatinVariants(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || !ContainsCyrillic(trimmed)) return [];

        var variants = new List<string>
        {
            Romanize(trimmed, gAsH: true),
            Romanize(trimmed, gAsH: false),
        };
        if (Exceptions.TryGetValue(trimmed.ToLowerInvariant(), out var extra))
        {
            variants.AddRange(extra);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trimmed };
        foreach (var v in variants)
        {
            if (v.Length == 0 || !seen.Add(v)) continue;
            result.Add(v);
        }
        return result;
    }

    private static string Romanize(string text, bool gAsH)
    {
        var sb = new StringBuilder(text.Length * 2);
        var atWordStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lower = char.ToLowerInvariant(c);
            var upper = char.IsUpper(c);

            if (lower is '’' or '\'' or '`')
            {
                atWordStart = false;
                continue;
            }

            // зг -> zgh, to keep it distinct from ж (zh).
            if (lower == 'з' && i + 1 < text.Length && char.ToLowerInvariant(text[i + 1]) == 'г')
            {
                sb.Append(Case("zgh", upper));
                i++;
                atWordStart = false;
                continue;
            }

            string? latin = lower switch
            {
                'г' => gAsH ? "h" : "g",
                _ when atWordStart && InitialMap.TryGetValue(lower, out var ini) => ini,
                _ when Map.TryGetValue(lower, out var mid) => mid,
                _ => null,
            };

            if (latin is null)
            {
                sb.Append(c);
                atWordStart = !char.IsLetter(c);
                continue;
            }

            sb.Append(Case(latin, upper));
            atWordStart = false;
        }
        return sb.ToString();
    }

    private static string Case(string latin, bool upperFirst)
    {
        if (!upperFirst || latin.Length == 0) return latin;
        return string.Create(latin.Length, latin, static (span, src) =>
        {
            src.AsSpan().CopyTo(span);
            span[0] = char.ToUpperInvariant(span[0]);
        });
    }

    // Aliases to seed at creation for a given kind: transliteration only adds signal for names
    // (person/place); concepts/orgs are skipped (often acronyms or already cross-lingual).
    public static IReadOnlyList<string> SeedAliasesFor(string kind, string canonical) =>
        kind is EntityKind.Person or EntityKind.Place
            ? CyrillicToLatinVariants(canonical)
            : [];
}
