using System.Globalization;
using System.Text;
using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

/// <summary>
/// Builds the "## Sources" block of attachment extractions.
/// Visual media (image, video) render as visible embed + italic caption.
/// Audio, URL, and file render as collapsed [!source]- callouts.
/// Attachments render in chronological order; the user's own text lives under "## Origin", not here.
/// </summary>
public static class SourcesRenderer
{
    public static string Render(IReadOnlyList<Attachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var sb = new StringBuilder();
        sb.AppendLine("## Sources");
        sb.AppendLine();

        var anyContent = false;

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

    private static string RenderAttachment(Attachment att) => att.Mode switch
    {
        AttachmentMode.Reference => RenderReference(att),
        AttachmentMode.Metadata  => RenderUrl(att, includeThumbnail: true),
        _ => att.Kind switch
        {
            AttachmentKind.Image => RenderImage(att),
            AttachmentKind.Voice => RenderVoice(att),
            AttachmentKind.Url   => RenderUrl(att),
            AttachmentKind.File  => att.MimeType is not null &&
                                    att.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                        ? RenderVideo(att)
                                        : RenderFile(att),
            _ => string.Empty,
        },
    };

    private static string RenderReference(Attachment att)
    {
        var (emoji, label) = ReferenceFace(att);
        if (att.Kind == AttachmentKind.Url)
        {
            var url = att.Url ?? string.Empty;
            return $"> [!source]- {emoji} {label} — [{Escape(url)}]({url})\n";
        }
        var filename = NormaliseFilename(att);
        return $"> [!source]- {emoji} {label} — ![[{filename}]]\n";
    }

    private static (string Emoji, string Label) ReferenceFace(Attachment att)
    {
        var mime = att.MimeType ?? string.Empty;
        if (att.Kind == AttachmentKind.Url) return ("🔗", "Reference");
        if (att.Kind == AttachmentKind.Voice || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return ("🎵", "Reference");
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return ("📹", "Reference");
        if (att.Kind == AttachmentKind.Image || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ("🖼️", "Reference");
        return ("📄", "Reference");
    }

    private static string RenderImage(Attachment att)
    {
        var sb = new StringBuilder();
        var filename = NormaliseFilename(att);
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- Image — ![[{filename}]]\n");
        if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"> *vision extraction failed: {SanitizeOneLine(att.ExtractionError ?? "unknown")}*\n");
        }
        else if (!string.IsNullOrWhiteSpace(att.ExtractedText))
        {
            AppendCalloutBody(sb, att.ExtractedText!);
        }
        else
        {
            sb.AppendLine("> *no caption*");
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

    private static string RenderUrl(Attachment att, bool includeThumbnail = false)
    {
        var meta = ReadUrlExtra(att);
        var url = meta.CanonicalUrl ?? att.Url ?? string.Empty;

        string heading;
        if (!string.IsNullOrWhiteSpace(meta.Title))
        {
            var bracket = !string.IsNullOrWhiteSpace(meta.ProviderName) ? $"[{meta.ProviderName}] " : "";
            var by      = !string.IsNullOrWhiteSpace(meta.AuthorName)   ? $" — {meta.AuthorName}"   : "";
            heading = $"{bracket}{meta.Title}{by}";
        }
        else
        {
            heading = url;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"> [!source]- URL — [{Escape(heading)}]({url})\n");

        if (includeThumbnail && !string.IsNullOrWhiteSpace(meta.ThumbnailUrl))
        {
            sb.Append(CultureInfo.InvariantCulture, $"> ![]({meta.ThumbnailUrl})\n");
        }

        if (!string.IsNullOrWhiteSpace(meta.Description))
        {
            AppendCalloutBody(sb, meta.Description!);
        }
        else if (!string.IsNullOrWhiteSpace(meta.MinimalReason))
        {
            sb.Append(CultureInfo.InvariantCulture, $"> *(URL captured, no preview — {SanitizeOneLine(meta.MinimalReason!)})*\n");
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

    private readonly record struct UrlMeta(
        string? Title,
        string? CanonicalUrl,
        string? ProviderName,
        string? AuthorName,
        string? Description,
        string? ThumbnailUrl,
        string? MinimalReason);

    private static UrlMeta ReadUrlExtra(Attachment att)
    {
        if (att.Extra is null) return default;
        var root = att.Extra.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return default;
        return new UrlMeta(
            Title: GetString(root, "title"),
            CanonicalUrl: GetString(root, "canonical_url") ?? GetString(root, "final_url"),
            ProviderName: GetString(root, "provider_name"),
            AuthorName: GetString(root, "author_name"),
            Description: GetString(root, "description"),
            ThumbnailUrl: GetString(root, "thumbnail_url"),
            MinimalReason: GetString(root, "minimal_reason"));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string SanitizeOneLine(string s)
    {
        var collapsed = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length > 240 ? collapsed[..240] + "…" : collapsed;
    }

    private static string Escape(string s) => s.Replace("]", "\\]");
}
