using System.Globalization;
using System.Text;
using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

/// <summary>
/// Builds the "## Sources" block that follows the synthesized prose.
/// Visual media (image, video) render as visible embed + italic caption.
/// Audio, URL, file, and user notes render as collapsed [!source]- callouts.
/// Order: user notes first if present, then attachments in chronological order.
/// </summary>
public static class SourcesRenderer
{
    public static string Render(
        string? userBody,
        IReadOnlyList<Attachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var sb = new StringBuilder();
        sb.AppendLine("## Sources");
        sb.AppendLine();

        var anyContent = false;

        if (!string.IsNullOrWhiteSpace(userBody))
        {
            AppendUserNotesCallout(sb, userBody);
            sb.AppendLine();
            anyContent = true;
        }

        var topLevel = attachments
            .Where(a => a.ParentAttachmentId is null)
            .OrderBy(a => a.CreatedAt);

        foreach (var att in topLevel)
        {
            var block = RenderAttachment(att);
            if (block.Length == 0) continue;
            sb.Append(block);
            if (!block.EndsWith("\n\n", StringComparison.Ordinal))
            {
                sb.AppendLine();
            }
            anyContent = true;
        }

        if (!anyContent)
        {
            sb.AppendLine("*No sources captured.*");
        }

        return sb.ToString();
    }

    private static string RenderAttachment(Attachment att) => att.Kind switch
    {
        AttachmentKind.Image => RenderImage(att),
        AttachmentKind.Voice => RenderVoice(att),
        AttachmentKind.Url   => RenderUrl(att),
        AttachmentKind.File  => att.MimeType is not null &&
                                att.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                    ? RenderVideo(att)
                                    : RenderFile(att),
        _ => string.Empty,
    };

    private static string RenderImage(Attachment att)
    {
        var sb = new StringBuilder();
        var filename = NormaliseFilename(att);
        sb.Append(CultureInfo.InvariantCulture, $"![[{filename}]]\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"*vision extraction failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}*\n");
        }
        else if (!string.IsNullOrWhiteSpace(att.ExtractedText))
        {
            sb.Append(CultureInfo.InvariantCulture, $"*{SanitizeOneLine(att.ExtractedText!)}*\n");
        }
        else
        {
            sb.AppendLine("*no caption*");
        }
        return sb.ToString();
    }

    private static string RenderVideo(Attachment att)
    {
        var sb = new StringBuilder();
        var filename = NormaliseFilename(att);
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- Video — ![[{filename}]]\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"> *video extraction failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}*\n");
        }
        else
        {
            AppendCalloutBody(sb, att.ExtractedText ?? "(no extracted text)");
        }
        return sb.ToString();
    }

    private static string RenderVoice(Attachment att)
    {
        var sb = new StringBuilder();
        var filename = NormaliseFilename(att);
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- Voice — ![[{filename}]]\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"> ASR failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}\n");
        }
        else
        {
            AppendCalloutBody(sb, att.ExtractedText ?? "(no transcript)");
        }
        return sb.ToString();
    }

    private static string RenderUrl(Attachment att)
    {
        var sb = new StringBuilder();
        var (title, canonicalUrl) = ReadUrlExtra(att);
        var url = canonicalUrl ?? att.Url ?? string.Empty;
        var heading = !string.IsNullOrWhiteSpace(title) ? title! : url;
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- URL — [{Escape(heading)}]({url})\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"> URL fetch failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}\n");
        }
        else if (!string.IsNullOrWhiteSpace(att.ExtractedText))
        {
            AppendCalloutBody(sb, att.ExtractedText!);
        }
        else
        {
            sb.AppendLine("> (no extracted text)");
        }
        return sb.ToString();
    }

    private static string RenderFile(Attachment att)
    {
        var sb = new StringBuilder();
        var filename = NormaliseFilename(att);
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- File — ![[{filename}]]\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"> File extraction failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}\n");
        }
        else if (!string.IsNullOrWhiteSpace(att.ExtractedText))
        {
            AppendCalloutBody(sb, att.ExtractedText!);
        }
        else
        {
            sb.AppendLine("> (no extracted text)");
        }
        return sb.ToString();
    }

    private static void AppendUserNotesCallout(StringBuilder sb, string body)
    {
        sb.AppendLine("> [!source]- User notes");
        AppendCalloutBody(sb, body);
    }

    private static void AppendCalloutBody(StringBuilder sb, string body)
    {
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("> ");
            sb.AppendLine(raw);
        }
    }

    private static string NormaliseFilename(Attachment att)
    {
        if (!string.IsNullOrWhiteSpace(att.Filename)) return att.Filename!;
        var key = att.StorageKey;
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    private static (string? Title, string? CanonicalUrl) ReadUrlExtra(Attachment att)
    {
        if (att.Extra is null) return (null, null);
        var root = att.Extra.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        string? title = null;
        string? canonical = null;
        if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            title = t.GetString();
        if (root.TryGetProperty("canonical_url", out var c) && c.ValueKind == JsonValueKind.String)
            canonical = c.GetString();
        return (title, canonical);
    }

    private static string SanitizeOneLine(string s)
    {
        var collapsed = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length > 240 ? collapsed[..240] + "…" : collapsed;
    }

    private static string Escape(string s) => s.Replace("]", "\\]");
}
