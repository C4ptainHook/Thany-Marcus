using System.Text.RegularExpressions;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public static partial class ThinkingStripper
{
    [GeneratedRegex(@"<think>[\s\S]*?</think>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    public static string Strip(string raw) =>
        string.IsNullOrEmpty(raw) ? raw : ThinkBlock().Replace(raw, string.Empty);
}
