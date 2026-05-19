using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed class DocumentRenderer : IAttachmentRenderer
{
    private static readonly HashSet<string> DocumentMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/msword",
        "application/zip",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
    };

    public bool Matches(Attachment attachment) =>
        attachment.Kind == AttachmentKind.File
        && attachment.MimeType is not null
        && DocumentMimes.Contains(attachment.MimeType);

    public string Render(Attachment attachment, IRenderContext context)
    {
        var filename = string.IsNullOrWhiteSpace(attachment.Filename) ? "untitled" : attachment.Filename!;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Document: {filename}\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"[document: {filename}]\n\n");
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
}
