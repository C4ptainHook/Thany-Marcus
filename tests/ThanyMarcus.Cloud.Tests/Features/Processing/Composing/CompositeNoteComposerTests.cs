using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing;

public sealed class CompositeNoteComposerTests
{
    [Fact]
    public async Task Composes_four_kind_composite_with_frontmatter_and_per_kind_blocks()
    {
        var note = NewNote();
        var t0 = Instant.FromUtc(2026, 5, 19, 10, 0);
        var url = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "article body",
            extraJson: "{\"title\":\"An Article\",\"url\":\"https://example.org/x\"}",
            createdAt: t0);
        var image = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractedText: "Description:\na sketch\n\nText:\nlabel",
            createdAt: t0.Plus(Duration.FromSeconds(1)));
        var voice = RendererTestSupport.NewAttachment(
            AttachmentKind.Voice, mime: "audio/wav",
            extractedText: "transcript",
            filename: "memo.wav",
            createdAt: t0.Plus(Duration.FromSeconds(2)));
        var doc = RendererTestSupport.NewAttachment(
            AttachmentKind.File, mime: "application/pdf",
            extractedText: "# doc body",
            filename: "spec.pdf",
            createdAt: t0.Plus(Duration.FromSeconds(3)));

        var composer = NewComposer(new FakeArtifactStore());
        var result = await composer.ComposeAsync(note, new[] { url, image, voice, doc }, previousBodyOutput: null, CancellationToken.None);

        result.ComposeTemplateVersion.ShouldBe(CompositeNoteComposer.ComposeTemplateVersion);
        result.Frontmatter.ShouldContain("compose_template: compose-v1");
        result.Body.ShouldStartWith("## User Notes\n\n");
        result.Body.ShouldContain(UserNotesPreserver.EmptyPlaceholder);
        result.Body.ShouldContain("\n\n## System Output\n\n");
        result.Body.ShouldContain("### Source: An Article");
        result.Body.ShouldContain("### Image");
        result.Body.ShouldContain("### Voice memo: memo.wav");
        result.Body.ShouldContain("### Document: spec.pdf");
    }

    [Fact]
    public async Task Reprocess_preserves_user_notes_block()
    {
        var note = NewNote();
        var url = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "body",
            extraJson: "{\"url\":\"https://example.org\"}");

        var previousBody = "---\nignored\n---\n\n## User Notes\n\nmy real notes\n\n## System Output\n\nOLD";
        var composer = NewComposer(new FakeArtifactStore());
        var result = await composer.ComposeAsync(note, new[] { url }, previousBody, CancellationToken.None);

        result.Body.ShouldContain("## User Notes\n\nmy real notes\n\n## System Output");
    }

    [Fact]
    public async Task Empty_composite_renders_no_attachments_marker()
    {
        var note = NewNote();
        var composer = NewComposer(new FakeArtifactStore());

        var result = await composer.ComposeAsync(note, Array.Empty<Attachment>(), null, CancellationToken.None);

        result.Body.ShouldContain("## System Output\n\n<!-- no attachments -->");
        result.Frontmatter.ShouldContain("attachment_kinds: []");
    }

    [Fact]
    public async Task Unknown_kind_throws_with_clear_message()
    {
        var note = NewNote();
        var bogus = RendererTestSupport.NewAttachment("unknown-kind", mime: "application/x-bogus");
        var composer = NewComposer(new FakeArtifactStore());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            composer.ComposeAsync(note, new[] { bogus }, null, CancellationToken.None));
        ex.Message.ShouldContain("no renderer matches");
    }

    [Fact]
    public async Task Failed_extraction_emits_hidden_marker_only()
    {
        var note = NewNote();
        var t0 = Instant.FromUtc(2026, 5, 19, 10, 0);
        var failedImage = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractionStatus: AttachmentExtractionStatus.Failed,
            extractionError: "ollama_json_parse",
            createdAt: t0);
        var goodUrl = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "article body",
            extraJson: "{\"url\":\"https://example.org\"}",
            createdAt: t0.Plus(Duration.FromSeconds(1)));

        var composer = NewComposer(new FakeArtifactStore());
        var result = await composer.ComposeAsync(note, new[] { failedImage, goodUrl }, null, CancellationToken.None);

        result.Body.ShouldContain("<!-- thany-marcus:attachment ");
        result.Body.ShouldContain("status=failed");
        result.Body.ShouldNotContain("### Image");
        result.Body.ShouldContain("### Source:");
        result.Frontmatter.ShouldContain("image");
        result.Frontmatter.ShouldContain("url");
    }

    [Fact]
    public async Task Cache_hit_renders_normally()
    {
        var note = NewNote();
        var cached = RendererTestSupport.NewAttachment(
            AttachmentKind.Url,
            extractedText: "cached body",
            extractionStatus: AttachmentExtractionStatus.Skipped,
            extraJson: "{\"url\":\"https://example.org\"}");

        var composer = NewComposer(new FakeArtifactStore());
        var result = await composer.ComposeAsync(note, new[] { cached }, null, CancellationToken.None);

        result.Body.ShouldContain("### Source:");
        result.Body.ShouldContain("cached body");
        result.Body.ShouldNotContain("status=skipped");
    }

    [Fact]
    public async Task Skipped_without_text_renders_hidden_marker()
    {
        var note = NewNote();
        var preflightSkipped = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractionStatus: AttachmentExtractionStatus.Skipped,
            extractedText: null,
            extractionError: "preflight_dimensions_out_of_range:50x50");

        var composer = NewComposer(new FakeArtifactStore());
        var result = await composer.ComposeAsync(note, new[] { preflightSkipped }, null, CancellationToken.None);

        result.Body.ShouldContain("status=skipped");
        result.Body.ShouldNotContain("### Image");
    }

    [Fact]
    public async Task Image_presign_failure_falls_back_without_throwing()
    {
        var note = NewNote();
        var img = RendererTestSupport.NewAttachment(
            AttachmentKind.Image, mime: "image/png",
            extractedText: "Description:\nfoo");

        var store = new FakeArtifactStore { FailDownloadUrl = true };
        var composer = NewComposer(store);
        var result = await composer.ComposeAsync(note, new[] { img }, null, CancellationToken.None);

        result.Body.ShouldContain($"[image: {img.StorageKey}]");
        result.Body.ShouldNotContain("![](");
    }

    private static CompositeNoteComposer NewComposer(IArtifactStore store) =>
        new(
            new IAttachmentRenderer[]
            {
                new UrlRenderer(),
                new ImageRenderer(),
                new AudioRenderer(),
                new VideoRenderer(),
                new DocumentRenderer(),
            },
            new FailedHiddenRenderer(),
            store,
            NullLogger<CompositeNoteComposer>.Instance);

    private static Note NewNote() => new()
    {
        Id           = Guid.CreateVersion7(),
        ClientNoteId = "cn",
        CapturedAt   = Instant.FromUtc(2026, 5, 19, 10, 0),
        BodyInput    = "ignored",
        Status       = NoteStatus.Processing,
        LlmMode      = "safe",
        CreatedAt    = Instant.FromUtc(2026, 5, 19, 10, 0),
        UpdatedAt    = Instant.FromUtc(2026, 5, 19, 10, 0),
    };
}
