using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public static class RawExtractions
{
    public static string Concatenate(Note note, IReadOnlyList<Attachment> attachments)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(note.BodyInput))
        {
            sb.Append(note.BodyInput);
            sb.Append("\n\n");
        }

        foreach (var att in attachments
            .Where(a => a.ParentAttachmentId is null)
            .OrderBy(a => a.CreatedAt))
        {
            if (att.ExtractionStatus != AttachmentExtractionStatus.Extracted) continue;
            if (string.IsNullOrWhiteSpace(att.ExtractedText)) continue;
            sb.Append(att.ExtractedText);
            sb.Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }
}
