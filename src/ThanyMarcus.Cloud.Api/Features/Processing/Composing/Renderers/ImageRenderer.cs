using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed class ImageRenderer : IAttachmentRenderer
{
    public bool Matches(Attachment attachment) =>
        attachment.Kind == AttachmentKind.Image;

    public string Render(Attachment attachment, IRenderContext context)
    {
        var sb = new StringBuilder();
        sb.Append("### Image\n\n");
        var presigned = context.TryGetPresignedDownloadUrl(attachment.StorageKey);
        if (!string.IsNullOrEmpty(presigned))
        {
            sb.Append(CultureInfo.InvariantCulture, $"![]({presigned})\n\n");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"[image: {attachment.StorageKey}]\n\n");
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
}
