using System.Text;
using System.Text.RegularExpressions;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static partial class WikilinkWeaver
{
    [GeneratedRegex(@"\[\[(.+?)\]\]")]
    private static partial Regex ExistingLink();

    public static string Weave(string body, IReadOnlyList<string>? targets, int max) =>
        Apply(body, targets, max, inline: true);

    public static string AppendRelated(string body, IReadOnlyList<string>? targets, int max) =>
        Apply(body, targets, max, inline: false);

    private static string Apply(string body, IReadOnlyList<string>? targets, int max, bool inline)
    {
        ArgumentNullException.ThrowIfNull(body);

        var normalised = Normalise(targets, max);
        if (normalised.Count == 0) return body;

        var protectedSpans = new List<(int Start, int End)>();
        var alreadyLinked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExistingLink().Matches(body))
        {
            protectedSpans.Add((m.Index, m.Index + m.Length));
            var inner = m.Groups[1].Value;
            var pipe = inner.IndexOf('|');
            var targetText = (pipe >= 0 ? inner[..pipe] : inner).Trim();
            if (targetText.Length > 0) alreadyLinked.Add(targetText);
        }

        var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = body;

        if (inline)
        {
            var claimed = new List<(int Start, int End)>(protectedSpans);
            var insertions = new List<(int Start, int End)>();

            foreach (var target in normalised.OrderByDescending(t => t.Length))
            {
                if (alreadyLinked.Contains(target)) { satisfied.Add(target); continue; }
                var start = FindFirstLiteral(body, target, claimed);
                if (start < 0) continue;
                var end = start + target.Length;
                insertions.Add((start, end));
                claimed.Add((start, end));
                satisfied.Add(target);
            }

            if (insertions.Count > 0)
            {
                insertions.Sort((a, b) => a.Start.CompareTo(b.Start));
                var sb = new StringBuilder(body.Length + insertions.Count * 4);
                var last = 0;
                foreach (var (start, end) in insertions)
                {
                    sb.Append(body, last, start - last);
                    sb.Append("[[").Append(body, start, end - start).Append("]]");
                    last = end;
                }
                sb.Append(body, last, body.Length - last);
                result = sb.ToString();
            }
        }
        else
        {
            foreach (var target in normalised)
            {
                if (alreadyLinked.Contains(target)) satisfied.Add(target);
            }
        }

        var fallback = normalised.Where(t => !satisfied.Contains(t)).ToList();
        if (fallback.Count > 0)
        {
            result += "\n\nRelated: " + string.Join(" · ", fallback.Select(t => $"[[{t}]]"));
        }

        return result;
    }

    private static List<string> Normalise(IReadOnlyList<string>? targets, int max)
    {
        var result = new List<string>();
        if (targets is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in targets)
        {
            if (raw is null) continue;
            var t = raw.Trim().Trim('[', ']').TrimStart('#').Trim();
            if (t.Length == 0) continue;
            if (!seen.Add(t)) continue;
            result.Add(t);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static int FindFirstLiteral(string body, string target, List<(int Start, int End)> claimed)
    {
        var from = 0;
        while (from <= body.Length - target.Length)
        {
            var idx = body.IndexOf(target, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            var end = idx + target.Length;

            var leftOk = idx == 0 || !IsWordChar(body[idx - 1]);
            var rightOk = end >= body.Length || !IsWordChar(body[end]);

            var overlaps = false;
            foreach (var (start, spanEnd) in claimed)
            {
                if (idx < spanEnd && end > start) { overlaps = true; break; }
            }

            if (leftOk && rightOk && !overlaps) return idx;
            from = idx + 1;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);
}
