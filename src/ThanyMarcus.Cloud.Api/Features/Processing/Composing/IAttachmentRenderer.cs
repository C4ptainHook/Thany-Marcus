using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

public interface IAttachmentRenderer
{
    bool Matches(Attachment attachment);
    string Render(Attachment attachment, IRenderContext context);
}
