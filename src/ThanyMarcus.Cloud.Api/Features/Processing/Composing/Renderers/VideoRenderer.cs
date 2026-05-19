using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed class VideoRenderer : IAttachmentRenderer
{
    public bool Matches(Attachment attachment) =>
        attachment.Kind == AttachmentKind.File
        && attachment.MimeType is not null
        && attachment.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public string Render(Attachment attachment, IRenderContext context)
    {
        var label = string.IsNullOrWhiteSpace(attachment.Filename)
            ? attachment.StorageKey
            : attachment.Filename!;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(attachment.Filename))
        {
            sb.Append(CultureInfo.InvariantCulture, $"### Video: {attachment.Filename}\n\n");
        }
        else
        {
            sb.Append("### Video\n\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"[video: {label}]\n\n");

        var children = context.ChildrenFor(attachment.Id);
        var audio = children.FirstOrDefault(c => c.Kind == AttachmentKind.Voice);
        var keyframes = children
            .Where(c => c.Kind == AttachmentKind.Image)
            .OrderBy(c => c.CreatedAt)
            .ToList();

        if (audio is not null)
        {
            sb.Append("**Audio:**\n");
            sb.Append(RenderChildBlock(audio, context));
            sb.Append('\n');
        }

        if (keyframes.Count > 0)
        {
            sb.Append("**Keyframes:**\n");
            for (var i = 0; i < keyframes.Count; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. ");
                sb.Append(RenderChildBlock(keyframes[i], context));
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string RenderChildBlock(Attachment child, IRenderContext context)
    {
        var raw = context.RenderChild(child).TrimEnd('\n');
        return raw.Length == 0 ? string.Empty : raw + "\n";
    }
}
