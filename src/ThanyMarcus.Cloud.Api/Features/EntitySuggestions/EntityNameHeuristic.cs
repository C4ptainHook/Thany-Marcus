namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public static class EntityNameHeuristic
{
    private const int MaxChars = 40;
    private const int MaxTokens = 4;
    private static readonly char[] DisqualifyingPunctuation = { '?', '!', ';', ':' };

    public static bool LooksLikeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxChars) return false;
        if (trimmed[^1] == '.') return false;
        if (trimmed.IndexOfAny(DisqualifyingPunctuation) >= 0) return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > MaxTokens) return false;

        return trimmed.Any(char.IsUpper);
    }
}
