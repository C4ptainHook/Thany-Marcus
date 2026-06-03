using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class SourcesRendererUrlTests
{
    [Fact]
    public void Renders_oembed_video_with_provider_and_author()
    {
        var att = Url(
            url: "https://www.youtube.com/watch?v=abc",
            extra: """
                {
                  "canonical_url": "https://www.youtube.com/watch?v=abc",
                  "title": "Cool Video",
                  "provider_name": "YouTube",
                  "author_name": "Channel",
                  "description": "A short caption",
                  "minimal_reason": null
                }
                """,
            status: AttachmentExtractionStatus.Extracted);

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain(@"> [!source]- URL — [[YouTube\] Cool Video — Channel](https://www.youtube.com/watch?v=abc)");
        md.ShouldContain("A short caption");
    }

    [Fact]
    public void Renders_og_only_url_without_provider_brackets()
    {
        var att = Url(
            url: "https://obsidian.md/",
            extra: """
                {
                  "canonical_url": "https://obsidian.md/",
                  "title": "Obsidian",
                  "description": "Sharpen your thinking.",
                  "minimal_reason": null
                }
                """,
            status: AttachmentExtractionStatus.Extracted);

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- URL — [Obsidian](https://obsidian.md/)");
        md.ShouldContain("Sharpen your thinking.");
        md.ShouldNotContain("URL fetch failed");
    }

    [Fact]
    public void Minimal_reason_renders_no_preview_line()
    {
        var att = Url(
            url: "https://example.com/404",
            extra: """
                {
                  "canonical_url": "https://example.com/404",
                  "minimal_reason": "http: 404"
                }
                """,
            status: AttachmentExtractionStatus.ExtractedMinimal);

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- URL — [https://example.com/404](https://example.com/404)");
        md.ShouldContain("URL captured, no preview — http: 404");
        md.ShouldNotContain("URL fetch failed");
    }

    private static Attachment Url(string url, string extra, string status)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = Guid.NewGuid(),
            ClientAttachmentId = "u",
            Kind = AttachmentKind.Url,
            StorageProvider = "external",
            StorageBucket = "",
            StorageKey = "external://x",
            Url = url,
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = status,
            Extra = JsonDocument.Parse(extra),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
