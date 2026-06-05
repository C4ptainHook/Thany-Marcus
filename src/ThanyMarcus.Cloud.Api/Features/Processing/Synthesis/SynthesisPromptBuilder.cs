using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class SynthesisPromptBuilder
{
    public static string Build(
        string systemBody,
        string? userBody,
        IReadOnlyList<SynthesisInput> attachmentInputs,
        EssenceForm form,
        EssenceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(systemBody);
        ArgumentNullException.ThrowIfNull(attachmentInputs);
        ArgumentNullException.ThrowIfNull(budget);

        var sb = new StringBuilder();
        sb.AppendLine(systemBody.TrimEnd());
        sb.AppendLine();

        sb.AppendLine("Inputs:");
        sb.AppendLine();
        sb.AppendLine("User notes:");
        if (string.IsNullOrWhiteSpace(userBody))
        {
            sb.AppendLine("<empty/>");
        }
        else
        {
            sb.AppendLine(userBody.Trim());
        }
        sb.AppendLine();

        foreach (var input in attachmentInputs)
        {
            if (input.Mode == AttachmentMode.Reference)
            {
                continue;
            }

            if (input.Mode == AttachmentMode.Metadata)
            {
                sb.Append("<reference id=\"").Append(input.Id ?? "att")
                  .Append("\" title=\"").Append(EscapeAttr(input.Title ?? ""))
                  .Append("\" description=\"").Append(EscapeAttr(input.Description ?? ""))
                  .Append("\" url=\"").Append(EscapeAttr(input.Url ?? ""))
                  .AppendLine("\"/>");
                sb.AppendLine();
                continue;
            }

            var heading = input.Kind switch
            {
                "voice" => "Voice transcript:",
                "image" => "Image caption:",
                "url"   => "URL extract:",
                "file"  => "File extract:",
                _       => $"{input.Kind} extract:",
            };
            sb.AppendLine(heading);
            if (input.Content is null)
            {
                sb.Append("<input failed kind=\"").Append(input.Kind).Append("\" reason=\"")
                  .Append(EscapeAttr(input.FailureReason ?? "unknown"))
                  .AppendLine("\"/>");
            }
            else
            {
                sb.AppendLine(input.Content.Trim());
            }
            sb.AppendLine();
        }

        AppendOutputContract(sb, form, budget);
        sb.AppendLine("/no_think");
        return sb.ToString();
    }

    private static void AppendOutputContract(StringBuilder sb, EssenceForm form, EssenceBudget budget)
    {
        var units = budget.Units.ToString(CultureInfo.InvariantCulture);
        sb.AppendLine("Respond with a single JSON object and nothing else — no Markdown, no code fences, no commentary.");
        sb.AppendLine("The object has these fields:");
        sb.AppendLine("- \"title\": a concise 4–8 word title, with no leading '#'.");
        sb.AppendLine("- \"tags\": 1–6 short topic tags, lowercase, no '#', words joined by '-'.");
        sb.AppendLine("- \"wikilinks\": the core ideas, names, and projects to link, each the bare target text without brackets.");
        sb.AppendLine(FormField(form, units, budget.MaxColumns.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine("Mention the wikilink targets naturally in the text; do not add [[ ]] brackets yourself.");
        sb.AppendLine("Distil, do not re-narrate: use at most " + units + " " + UnitNoun(form) + " and never repeat an idea.");
    }

    private static string FormField(EssenceForm form, string units, string maxColumns) => form switch
    {
        EssenceForm.Prose =>
            $"- \"sentences\": an array of at most {units} complete sentences that together read as one short paragraph.",
        EssenceForm.Bullets =>
            $"- \"bullets\": an array of at most {units} short bullet points, one discrete idea each.",
        EssenceForm.Checklist =>
            $"- \"items\": an array of at most {units} objects {{ \"text\", \"checked\" }}; set \"checked\" true only when the inputs say it is already done.",
        EssenceForm.Table =>
            $"- \"columns\": 1–{maxColumns} column headers. \"rows\": an array of at most {units} objects {{ \"cells\" }} whose cells align to the columns in order.",
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unknown form"),
    };

    private static string UnitNoun(EssenceForm form) => form switch
    {
        EssenceForm.Prose => "sentences",
        EssenceForm.Bullets => "bullets",
        EssenceForm.Checklist => "items",
        EssenceForm.Table => "rows",
        _ => "items",
    };

    private static string EscapeAttr(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
}
