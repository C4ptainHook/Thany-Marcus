using System.Globalization;
using System.Text;
using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed class UrlRenderer : IAttachmentRenderer
{
    private const int MaxHeadingLength = 80;

    public bool Matches(Attachment attachment) =>
        attachment.Kind == AttachmentKind.Url;

    public string Render(Attachment attachment, IRenderContext context)
    {
        var (title, canonicalUrl) = ReadExtra(attachment);
        var sourceUrl = canonicalUrl ?? attachment.Url ?? ReadUrlFromExtra(attachment) ?? string.Empty;

        var heading = title;
        if (string.IsNullOrWhiteSpace(heading))
        {
            heading = canonicalUrl;
        }
        if (string.IsNullOrWhiteSpace(heading))
        {
            heading = sourceUrl;
        }
        var truncatedHeading = Truncate(heading ?? string.Empty, MaxHeadingLength);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Source: {truncatedHeading}\n\n");
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            sb.Append(CultureInfo.InvariantCulture, $"Source: {sourceUrl}\n\n");
        }
        if (string.IsNullOrWhiteSpace(attachment.ExtractedText))
        {
            sb.Append("<!-- no extracted content -->\n");
        }
        else
        {
            sb.Append(ComposerEscaping.EscapeSystemOutputHeading(attachment.ExtractedText!));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static (string? Title, string? CanonicalUrl) ReadExtra(Attachment att)
    {
        if (att.Extra is null) return (null, null);
        string? title = null;
        string? canonical = null;
        var root = att.Extra.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                title = t.GetString();
            }
            if (root.TryGetProperty("canonical_url", out var c) && c.ValueKind == JsonValueKind.String)
            {
                canonical = c.GetString();
            }
        }
        return (title, canonical);
    }

    private static string? ReadUrlFromExtra(Attachment att)
    {
        if (att.Extra is null) return null;
        var root = att.Extra.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
        {
            return u.GetString();
        }
        if (root.TryGetProperty("final_url", out var f) && f.ValueKind == JsonValueKind.String)
        {
            return f.GetString();
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
