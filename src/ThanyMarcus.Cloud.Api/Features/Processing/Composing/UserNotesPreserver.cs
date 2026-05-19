using System.Text.RegularExpressions;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

public static partial class UserNotesPreserver
{
    public const string EmptyPlaceholder = "<!-- user notes -->";

    [GeneratedRegex(@"## User Notes\r?\n([\s\S]*?)\r?\n## System Output", RegexOptions.Compiled)]
    private static partial Regex Pattern();

    public static string Extract(string? previousBody)
    {
        if (string.IsNullOrEmpty(previousBody)) return EmptyPlaceholder;
        var m = Pattern().Match(previousBody);
        if (!m.Success) return EmptyPlaceholder;
        var preserved = m.Groups[1].Value.Trim();
        return preserved.Length == 0 ? EmptyPlaceholder : preserved;
    }
}
