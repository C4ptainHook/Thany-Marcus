using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed class AudioRenderer : IAttachmentRenderer
{
    public bool Matches(Attachment attachment) =>
        attachment.Kind == AttachmentKind.Voice;

    public string Render(Attachment attachment, IRenderContext context)
    {
        var sb = new StringBuilder();
        var label = !string.IsNullOrWhiteSpace(attachment.Filename)
            ? attachment.Filename!
            : attachment.StorageKey;
        if (!string.IsNullOrWhiteSpace(attachment.Filename))
        {
            sb.Append(CultureInfo.InvariantCulture, $"### Voice memo: {attachment.Filename}\n\n");
        }
        else
        {
            sb.Append("### Voice memo\n\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"[audio: {label}]\n\n");
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
