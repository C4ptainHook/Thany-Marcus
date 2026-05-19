using System.Text;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public static class ProjectPathSanitizer
{
    public static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ' ')
            {
                sb.Append(ch);
            }
        }
        var trimmed = sb.ToString().Trim();
        return trimmed.Length == 0 ? "Inbox" : trimmed;
    }
}
