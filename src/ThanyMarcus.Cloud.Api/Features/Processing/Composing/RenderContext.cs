using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

internal sealed class RenderContext : IRenderContext
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> childrenByParent;
    private readonly IReadOnlyDictionary<string, string?> presignedByKey;
    private readonly Func<Attachment, IRenderContext, string> renderChild;

    public RenderContext(
        IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> childrenByParent,
        IReadOnlyDictionary<string, string?> presignedByKey,
        Func<Attachment, IRenderContext, string> renderChild)
    {
        this.childrenByParent = childrenByParent;
        this.presignedByKey = presignedByKey;
        this.renderChild = renderChild;
    }

    public IReadOnlyList<Attachment> ChildrenFor(Guid parentAttachmentId) =>
        childrenByParent.TryGetValue(parentAttachmentId, out var list)
            ? list
            : Array.Empty<Attachment>();

    public string? TryGetPresignedDownloadUrl(string storageKey) =>
        presignedByKey.TryGetValue(storageKey, out var url) ? url : null;

    public string RenderChild(Attachment child) => renderChild(child, this);
}
