using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

public sealed partial class CompositeNoteComposer
{
    public const string ComposeTemplateVersion = "compose-v1";
    public static readonly TimeSpan ImagePresignedUrlTtl = TimeSpan.FromDays(7);

    private readonly IAttachmentRenderer[] renderers;
    private readonly FailedHiddenRenderer failedRenderer;
    private readonly IArtifactStore artifactStore;
    private readonly ILogger<CompositeNoteComposer> logger;

    public CompositeNoteComposer(
        IEnumerable<IAttachmentRenderer> renderers,
        FailedHiddenRenderer failedRenderer,
        IArtifactStore artifactStore,
        ILogger<CompositeNoteComposer> logger)
    {
        this.renderers = renderers.ToArray();
        this.failedRenderer = failedRenderer;
        this.artifactStore = artifactStore;
        this.logger = logger;

        ValidateRendererCoverage(this.renderers);
    }

    public async Task<ComposedNote> ComposeAsync(
        Note note,
        IReadOnlyList<Attachment> attachments,
        string? previousBodyOutput,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(attachments);

        var topLevel = attachments
            .Where(a => a.ParentAttachmentId is null)
            .OrderBy(a => a.CreatedAt)
            .ToList();

        var childrenByParent = attachments
            .Where(a => a.ParentAttachmentId is not null)
            .GroupBy(a => a.ParentAttachmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Attachment>)g.OrderBy(a => a.CreatedAt).ToList());

        var presignedByKey = await PresignImageUrlsAsync(attachments, ct).ConfigureAwait(false);

        var context = new RenderContext(childrenByParent, presignedByKey, RenderSingle);

        var frontmatter = FrontmatterBuilder.Build(note, topLevel, ComposeTemplateVersion);
        var preservedNotes = UserNotesPreserver.Extract(previousBodyOutput);

        var body = StitchBody(topLevel, context, preservedNotes);

        return new ComposedNote(frontmatter, body, ComposeTemplateVersion);
    }

    private string StitchBody(
        List<Attachment> topLevel,
        IRenderContext context,
        string preservedUserNotes)
    {
        var sb = new StringBuilder();
        sb.Append("## User Notes\n\n");
        sb.Append(preservedUserNotes);
        sb.Append("\n\n## System Output\n\n");

        if (topLevel.Count == 0)
        {
            sb.Append("<!-- no attachments -->\n");
            return sb.ToString();
        }

        for (var i = 0; i < topLevel.Count; i++)
        {
            var rendered = RenderSingle(topLevel[i], context);
            sb.Append(rendered);
            if (i < topLevel.Count - 1 && !rendered.EndsWith("\n\n", StringComparison.Ordinal))
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private string RenderSingle(Attachment att, IRenderContext context)
    {
        if (ShouldRouteToFailedRenderer(att))
        {
            return failedRenderer.Render(att);
        }

        IAttachmentRenderer? match = null;
        for (var i = 0; i < renderers.Length; i++)
        {
            if (renderers[i].Matches(att))
            {
                match = renderers[i];
                break;
            }
        }
        if (match is null)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "no renderer matches attachment kind={0} mime={1} id={2}",
                att.Kind,
                att.MimeType ?? "<null>",
                att.Id));
        }
        return match.Render(att, context);
    }

    // skipped-with-text is a cache hit (renders normally); skipped-without-text is a preflight reject (hidden)
    private static bool ShouldRouteToFailedRenderer(Attachment att) =>
        att.ExtractionStatus == AttachmentExtractionStatus.Failed
        || (att.ExtractionStatus == AttachmentExtractionStatus.Skipped
            && string.IsNullOrWhiteSpace(att.ExtractedText));

    private async Task<IReadOnlyDictionary<string, string?>> PresignImageUrlsAsync(
        IReadOnlyList<Attachment> attachments,
        CancellationToken ct)
    {
        var imageKeys = attachments
            .Where(a => a.Kind == AttachmentKind.Image
                         && !ShouldRouteToFailedRenderer(a)
                         && !string.IsNullOrEmpty(a.StorageKey))
            .Select(a => a.StorageKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in imageKeys)
        {
            try
            {
                var presigned = await artifactStore.IssueDownloadUrlAsync(key, ImagePresignedUrlTtl, ct)
                    .ConfigureAwait(false);
                result[key] = presigned.Url.ToString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPresignFailed(logger, ex, key);
                result[key] = null;
            }
        }
        return result;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "presign download url failed for storage key {StorageKey}; rendering image without embed")]
    private static partial void LogPresignFailed(ILogger logger, Exception ex, string storageKey);

    private static void ValidateRendererCoverage(IReadOnlyList<IAttachmentRenderer> renderers)
    {
        var fixtures = new (string Kind, string? Mime)[]
        {
            (AttachmentKind.Url,   null),
            (AttachmentKind.Image, "image/png"),
            (AttachmentKind.Voice, "audio/wav"),
            (AttachmentKind.File,  "application/pdf"),
            (AttachmentKind.File,  "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            (AttachmentKind.File,  "video/mp4"),
        };

        foreach (var (kind, mime) in fixtures)
        {
            var probe = new Attachment
            {
                Id                 = Guid.Empty,
                NoteId             = Guid.Empty,
                ClientAttachmentId = "fixture",
                Kind               = kind,
                StorageProvider    = "fixture",
                StorageBucket      = "fixture",
                StorageKey         = "fixture",
                MimeType           = mime,
                Status             = AttachmentStatus.Uploaded,
                ExtractionStatus   = AttachmentExtractionStatus.Extracted,
                Extra              = JsonDocument.Parse("{}"),
                CreatedAt          = Instant.MinValue,
                UpdatedAt          = Instant.MinValue,
            };
            var matches = renderers.Count(r => r.Matches(probe));
            if (matches != 1)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "renderer coverage broken for kind={0} mime={1}: expected exactly one match, got {2}",
                    kind, mime ?? "<null>", matches));
            }
        }
    }
}
