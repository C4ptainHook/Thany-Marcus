using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

public sealed class CompositeMarkdownAssemblerTests
{
    [Fact]
    public void Assembles_frontmatter_content_and_attachment_sections()
    {
        var note = NewNote("Reading Berlin trains, see voice note");
        var att = NewAttachment(note.Id, AttachmentKind.Voice, "memo.wav", extracted: null,
            extractionStatus: AttachmentExtractionStatus.Skipped);

        var enriched = new CompositeEnrichmentResult(
            SuggestedProject:  "Travel",
            BodyAnchors:       [new WikilinkAnchor("Berlin", 8, 14, "Berlin")],
            AttachmentAnchors: [],
            Tags:              ["travel", "berlin"],
            InputTokens:       100,
            OutputTokens:      50);

        var md = CompositeMarkdownAssembler.Assemble(note, [att], enriched, "unsafe_anthropic");

        md.ShouldContain("source: composite");
        md.ShouldContain("llm_mode: unsafe_anthropic");
        md.ShouldContain("tags: [travel, berlin]");
        md.ShouldContain("## Content");
        md.ShouldContain("[[Berlin|Berlin]]");
        md.ShouldContain("## Attachments");
        md.ShouldContain($"![[attachment:{att.Id}]]");
        md.ShouldContain("_extraction skipped_");
    }

    [Fact]
    public void Url_attachment_renders_inline_link_not_attachment_placeholder()
    {
        var note = NewNote("see article");
        var att = NewAttachment(note.Id, AttachmentKind.Url, filename: null,
            extracted: "Article body text",
            extractionStatus: AttachmentExtractionStatus.Extracted,
            extraJson: "{\"url\":\"https://example.com/article\"}");

        var enriched = new CompositeEnrichmentResult(null, [], [], [], null, null);

        var md = CompositeMarkdownAssembler.Assemble(note, [att], enriched, "unsafe_anthropic");

        md.ShouldContain("<https://example.com/article>");
        md.ShouldNotContain($"![[attachment:{att.Id}]]");
        md.ShouldContain("## Extracted");
        md.ShouldContain("Article body text");
    }

    [Fact]
    public void No_anchors_leaves_body_unchanged()
    {
        var note = NewNote("just a thought");
        var enriched = new CompositeEnrichmentResult(null, [], [], [], null, null);

        var md = CompositeMarkdownAssembler.Assemble(note, [], enriched, "safe");
        md.ShouldContain("just a thought");
        md.ShouldNotContain("[[");
    }

    [Fact]
    public void Anchors_out_of_range_are_dropped()
    {
        var note = NewNote("short");
        var enriched = new CompositeEnrichmentResult(null,
            [
                new WikilinkAnchor("x", 0, 100, "X"),
                new WikilinkAnchor("ok", 0, 5, "Ok"),
            ], [], [], null, null);

        var md = CompositeMarkdownAssembler.Assemble(note, [], enriched, "safe");
        md.ShouldContain("[[Ok|ok]]");
        md.ShouldNotContain("[[X|x]]");
    }

    [Fact]
    public void SanitizeProject_strips_path_separators_and_special_chars()
    {
        CompositeMarkdownAssembler.SanitizeProject("Travel/2026").ShouldBe("Travel2026");
        CompositeMarkdownAssembler.SanitizeProject("../etc").ShouldBe("etc");
        CompositeMarkdownAssembler.SanitizeProject("   ").ShouldBe("Inbox");
    }

    private static Note NewNote(string body) => new()
    {
        Id           = Guid.CreateVersion7(),
        ClientNoteId = "cn-1",
        CapturedAt   = Instant.FromUtc(2026, 5, 18, 10, 0),
        BodyInput    = body,
        Status       = NoteStatus.Processing,
        CreatedAt    = Instant.FromUtc(2026, 5, 18, 10, 0),
        UpdatedAt    = Instant.FromUtc(2026, 5, 18, 10, 0),
    };

    private static Attachment NewAttachment(
        Guid noteId, string kind, string? filename, string? extracted,
        string extractionStatus, string extraJson = "{}") => new()
    {
        Id                 = Guid.CreateVersion7(),
        NoteId             = noteId,
        ClientAttachmentId = "a1",
        Kind               = kind,
        StorageProvider    = "s3",
        StorageBucket      = "test-bucket",
        StorageKey         = $"notes/{noteId}/{Guid.NewGuid()}.bin",
        Filename           = filename,
        Status             = AttachmentStatus.Uploaded,
        ExtractionStatus   = extractionStatus,
        ExtractedText      = extracted,
        Extra              = JsonDocument.Parse(extraJson),
        CreatedAt          = Instant.FromUtc(2026, 5, 18, 10, 0),
        UpdatedAt          = Instant.FromUtc(2026, 5, 18, 10, 0),
    };
}
