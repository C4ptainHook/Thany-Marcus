using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public sealed class EssenceParseException : Exception
{
    public EssenceParseException(string message, Exception? inner = null) : base(message, inner) { }
}

public static partial class EssenceRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [GeneratedRegex(@"[^\p{L}\p{Nd}_/-]+")]
    private static partial Regex TagInvalid();

    public static string Render(EssenceForm form, string json, EssenceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(budget);

        try
        {
            return form switch
            {
                EssenceForm.Prose     => RenderProse(Deserialize<ProseEssence>(json), budget),
                EssenceForm.Bullets   => RenderBullets(Deserialize<BulletsEssence>(json), budget),
                EssenceForm.Checklist => RenderChecklist(Deserialize<ChecklistEssence>(json), budget),
                EssenceForm.Table     => RenderTable(Deserialize<TableEssence>(json), budget),
                _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unknown form"),
            };
        }
        catch (JsonException ex)
        {
            throw new EssenceParseException($"essence JSON was malformed: {ex.Message}", ex);
        }
    }

    private static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        if (value is null) throw new EssenceParseException("essence JSON deserialized to null");
        return value;
    }

    private static string RenderProse(ProseEssence essence, EssenceBudget budget)
    {
        var sentences = Clamp(essence.Sentences, budget.Units)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (sentences.Count == 0) throw new EssenceParseException("prose essence had no sentences");
        return Compose(essence.Title, essence.Tags, budget, string.Join(' ', sentences), essence.Wikilinks);
    }

    private static string RenderBullets(BulletsEssence essence, EssenceBudget budget)
    {
        var bullets = Clamp(essence.Bullets, budget.Units)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();
        if (bullets.Count == 0) throw new EssenceParseException("bullets essence had no bullets");
        var body = string.Join('\n', bullets.Select(b => $"- {b}"));
        return Compose(essence.Title, essence.Tags, budget, body, essence.Wikilinks);
    }

    private static string RenderChecklist(ChecklistEssence essence, EssenceBudget budget)
    {
        var items = Clamp(essence.Items, budget.Units)
            .Where(i => !string.IsNullOrWhiteSpace(i.Text))
            .ToList();
        if (items.Count == 0) throw new EssenceParseException("checklist essence had no items");
        var body = string.Join('\n', items.Select(i => $"- [{(i.Checked ? "x" : " ")}] {i.Text.Trim()}"));
        return Compose(essence.Title, essence.Tags, budget, body, essence.Wikilinks);
    }

    private static string RenderTable(TableEssence essence, EssenceBudget budget)
    {
        var columns = Clamp(essence.Columns, budget.MaxColumns)
            .Select(c => c.Trim())
            .ToList();
        if (columns.Count == 0) throw new EssenceParseException("table essence had no columns");
        var rows = Clamp(essence.Rows, budget.Units).ToList();
        if (rows.Count == 0) throw new EssenceParseException("table essence had no rows");

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", columns.Select(EscapeCell))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).AppendLine(" |");
        foreach (var row in rows)
        {
            var cells = (row.Cells ?? Array.Empty<string>())
                .Take(columns.Count)
                .Select(c => EscapeCell(c?.Trim() ?? string.Empty))
                .ToList();
            while (cells.Count < columns.Count) cells.Add(string.Empty);
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }
        return Compose(essence.Title, essence.Tags, budget, sb.ToString().TrimEnd(), essence.Wikilinks, inlineWeave: false);
    }

    private static string Compose(
        string? title,
        IReadOnlyList<string>? tags,
        EssenceBudget budget,
        string body,
        IReadOnlyList<string>? wikilinks,
        bool inlineWeave = true)
    {
        var woven = inlineWeave
            ? WikilinkWeaver.Weave(body, wikilinks, budget.MaxWikilinks)
            : WikilinkWeaver.AppendRelated(body, wikilinks, budget.MaxWikilinks);

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(NormaliseTitle(title));

        var tagLine = RenderTags(tags, budget.MaxTags);
        if (tagLine.Length > 0) sb.AppendLine(tagLine);

        sb.AppendLine();
        sb.Append(woven);
        return sb.ToString();
    }

    private static string RenderTags(IReadOnlyList<string>? tags, int max)
    {
        if (tags is null) return string.Empty;
        var rendered = tags
            .Select(SanitiseTag)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .Select(t => "#" + t);
        return string.Join(' ', rendered);
    }

    private static string SanitiseTag(string raw)
    {
        var trimmed = raw.Trim().TrimStart('#').Trim();
        if (trimmed.Length == 0) return string.Empty;
        var collapsed = TagInvalid().Replace(trimmed, "-").Trim('-').ToLowerInvariant();
        return collapsed;
    }

    private static string NormaliseTitle(string? title)
    {
        var trimmed = title?.Trim().TrimStart('#').Trim();
        return string.IsNullOrEmpty(trimmed) ? "Untitled note" : trimmed;
    }

    private static string EscapeCell(string s) =>
        s.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static IEnumerable<T> Clamp<T>(IReadOnlyList<T>? items, int max) =>
        items is null ? Enumerable.Empty<T>() : items.Take(max);
}
