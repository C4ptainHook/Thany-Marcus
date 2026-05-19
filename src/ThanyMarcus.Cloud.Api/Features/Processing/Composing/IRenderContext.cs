using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

public interface IRenderContext
{
    IReadOnlyList<Attachment> ChildrenFor(Guid parentAttachmentId);
    string? TryGetPresignedDownloadUrl(string storageKey);
    string RenderChild(Attachment child);
}
