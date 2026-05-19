using System.Text.Json;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;

internal static class RendererTestSupport
{
    public static Attachment NewAttachment(
        string kind,
        string? mime = null,
        string? extractedText = null,
        string? filename = null,
        string? extractionError = null,
        string extractionStatus = AttachmentExtractionStatus.Extracted,
        Guid? parentAttachmentId = null,
        string? url = null,
        string extraJson = "{}",
        Instant? createdAt = null)
    {
        var now = createdAt ?? Instant.FromUtc(2026, 5, 19, 10, 0);
        return new Attachment
        {
            Id                 = Guid.CreateVersion7(),
            NoteId             = Guid.CreateVersion7(),
            ClientAttachmentId = "client",
            Kind               = kind,
            StorageProvider    = "test",
            StorageBucket      = "bucket",
            StorageKey         = $"notes/{Guid.NewGuid():N}.bin",
            MimeType           = mime,
            Filename           = filename,
            Status             = AttachmentStatus.Uploaded,
            ExtractionStatus   = extractionStatus,
            ExtractedText      = extractedText,
            ExtractionError    = extractionError,
            Extra              = JsonDocument.Parse(extraJson),
            ParentAttachmentId = parentAttachmentId,
            Url                = url,
            CreatedAt          = now,
            UpdatedAt          = now,
        };
    }

    public static IRenderContext NewContext(
        IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>>? children = null,
        IReadOnlyDictionary<string, string?>? presigned = null,
        Func<Attachment, IRenderContext, string>? renderChild = null)
    {
        return new TestRenderContext(
            children ?? new Dictionary<Guid, IReadOnlyList<Attachment>>(),
            presigned ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            renderChild ?? ((_, _) => string.Empty));
    }

    private sealed class TestRenderContext : IRenderContext
    {
        private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> children;
        private readonly IReadOnlyDictionary<string, string?> presigned;
        private readonly Func<Attachment, IRenderContext, string> renderChild;

        public TestRenderContext(
            IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> children,
            IReadOnlyDictionary<string, string?> presigned,
            Func<Attachment, IRenderContext, string> renderChild)
        {
            this.children = children;
            this.presigned = presigned;
            this.renderChild = renderChild;
        }

        public IReadOnlyList<Attachment> ChildrenFor(Guid parentAttachmentId) =>
            children.TryGetValue(parentAttachmentId, out var list) ? list : Array.Empty<Attachment>();

        public string? TryGetPresignedDownloadUrl(string storageKey) =>
            presigned.TryGetValue(storageKey, out var u) ? u : null;

        public string RenderChild(Attachment child) => renderChild(child, this);
    }
}
