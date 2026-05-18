using System.Globalization;
using System.Text;
using NodaTime;
using NodaTime.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public static class CompositeMarkdownAssembler
{
    public static string Assemble(
        Note note,
        IReadOnlyList<Attachment> attachments,
        CompositeEnrichmentResult enriched,
        string llmMode)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"source: composite");
        sb.AppendLine(CultureInfo.InvariantCulture, $"captured_at: {InstantPattern.ExtendedIso.Format(note.CapturedAt)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"llm_mode: {llmMode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"draft_id: {note.Id}");
        if (note.ClientNoteId is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"client_note_id: {note.ClientNoteId}");
        }
        if (enriched.Tags.Count > 0)
        {
            sb.Append("tags: [");
            sb.Append(string.Join(", ", enriched.Tags.Select(EscapeYamlScalar)));
            sb.AppendLine("]");
        }
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## Content");
        sb.AppendLine(ApplyAnchors(note.BodyInput, enriched.BodyAnchors));
        sb.AppendLine();

        if (attachments.Count > 0)
        {
            sb.AppendLine("## Attachments");
            foreach (var att in attachments)
            {
                AppendAttachmentEntry(sb, att);
            }
            sb.AppendLine();
        }

        var anyExtractions = attachments.Any(a =>
            a.ExtractionStatus == AttachmentExtractionStatus.Extracted &&
            !string.IsNullOrWhiteSpace(a.ExtractedText));
        if (anyExtractions)
        {
            sb.AppendLine("## Extracted");
            foreach (var att in attachments)
            {
                if (att.ExtractionStatus != AttachmentExtractionStatus.Extracted ||
                    string.IsNullOrWhiteSpace(att.ExtractedText))
                {
                    continue;
                }

                var attachmentAnchors = enriched.AttachmentAnchors
                    .FirstOrDefault(x => x.AttachmentId == att.Id)?.Anchors
                    ?? Array.Empty<WikilinkAnchor>();

                sb.AppendLine(CultureInfo.InvariantCulture, $"### {att.Filename ?? att.Id.ToString()}");
                sb.AppendLine();
                sb.AppendLine(ApplyAnchors(att.ExtractedText!, attachmentAnchors));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void AppendAttachmentEntry(StringBuilder sb, Attachment att)
    {
        var label = att.Filename ?? att.Id.ToString();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **{label}** (kind={att.Kind})");
        if (att.Kind == AttachmentKind.Url)
        {
            string? url = null;
            if (att.Extra.RootElement.TryGetProperty("url", out var u) &&
                u.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                url = u.GetString();
            }
            if (!string.IsNullOrWhiteSpace(url))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - <{url}>");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - ![[attachment:{att.Id}]]");
        }

        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed && att.ExtractionError is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - _extraction failed: {att.ExtractionError}_");
        }
        else if (att.ExtractionStatus == AttachmentExtractionStatus.Skipped)
        {
            sb.AppendLine("  - _extraction skipped_");
        }
    }

    private static string ApplyAnchors(string body, IReadOnlyList<WikilinkAnchor> anchors)
    {
        if (anchors.Count == 0) return body;

        var sorted = anchors
            .Where(a => a.Start >= 0 && a.End <= body.Length && a.End > a.Start)
            .OrderByDescending(a => a.Start)
            .ToList();

        var sb = new StringBuilder(body);
        foreach (var a in sorted)
        {
            var rendered = $"[[{a.Target}|{a.Text}]]";
            sb.Remove(a.Start, a.End - a.Start);
            sb.Insert(a.Start, rendered);
        }
        return sb.ToString();
    }

    private static string EscapeYamlScalar(string s) =>
        s.Contains(':', StringComparison.Ordinal) || s.Contains('#', StringComparison.Ordinal)
            ? $"\"{s.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : s;

    public static string SanitizeProject(string raw)
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
