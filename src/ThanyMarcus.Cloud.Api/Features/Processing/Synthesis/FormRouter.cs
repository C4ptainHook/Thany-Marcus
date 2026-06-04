using System.Text;
using System.Text.RegularExpressions;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static partial class FormRouter
{
    [GeneratedRegex(@"^\s*[-*]\s*\[[ xX]\]\s+\S", RegexOptions.Multiline)]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"\b(todo|to-do|to do|action item|next steps?|checklist)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaskWord();

    [GeneratedRegex(@"^\s*([-*•]|\d+[.)])\s+\S", RegexOptions.Multiline)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\s*\|.*\|.*$\r?\n\s*\|?\s*:?-{2,}", RegexOptions.Multiline)]
    private static partial Regex MarkdownTable();

    public static EssenceForm Pick(string? userBody, IReadOnlyList<SynthesisInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var text = Combine(userBody, inputs);
        if (string.IsNullOrWhiteSpace(text)) return EssenceForm.Prose;

        if (MarkdownTable().IsMatch(text)) return EssenceForm.Table;
        if (Checkbox().IsMatch(text) || TaskWord().IsMatch(text)) return EssenceForm.Checklist;
        if (BulletLine().Count(text) >= 2) return EssenceForm.Bullets;
        return EssenceForm.Prose;
    }

    private static string Combine(string? userBody, IReadOnlyList<SynthesisInput> inputs)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userBody)) sb.AppendLine(userBody);
        foreach (var input in inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.Content)) sb.AppendLine(input.Content);
        }
        return sb.ToString();
    }
}
