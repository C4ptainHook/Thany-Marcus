using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Synthesis;

public sealed class SourcesRendererModeTests
{
    [Fact]
    public void Reference_audio_renders_emoji_and_embed_not_transcript()
    {
        var att = Binary(AttachmentMode.Reference, AttachmentKind.Voice, "song.mp3", "audio/mpeg");

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- 🎵 Reference — ![[song.mp3]]");
        md.ShouldNotContain("Voice —");
        md.ShouldNotContain("transcript");
    }

    [Fact]
    public void Reference_video_uses_video_emoji()
    {
        var att = Binary(AttachmentMode.Reference, AttachmentKind.File, "clip.mp4", "video/mp4");

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- 📹 Reference — ![[clip.mp4]]");
    }

    [Fact]
    public void Reference_url_renders_link()
    {
        var att = new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = Guid.NewGuid(),
            ClientAttachmentId = "u",
            Kind = AttachmentKind.Url,
            Mode = AttachmentMode.Reference,
            StorageProvider = "external",
            StorageBucket = "",
            StorageKey = "external://x",
            Url = "https://example.com/song",
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = AttachmentExtractionStatus.Referenced,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        };

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- 🔗 Reference — [https://example.com/song](https://example.com/song)");
    }

    [Fact]
    public void Metadata_url_renders_og_card_with_thumbnail()
    {
        var att = new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = Guid.NewGuid(),
            ClientAttachmentId = "u",
            Kind = AttachmentKind.Url,
            Mode = AttachmentMode.Metadata,
            StorageProvider = "external",
            StorageBucket = "",
            StorageKey = "external://x",
            Url = "https://example.com/p",
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = AttachmentExtractionStatus.Extracted,
            Extra = JsonDocument.Parse("""
                {
                  "canonical_url": "https://example.com/p",
                  "title": "Bookmarked",
                  "description": "card blurb",
                  "thumbnail_url": "https://cdn/thumb.jpg",
                  "minimal_reason": null
                }
                """),
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
        };

        var md = SourcesRenderer.Render(new[] { att });

        md.ShouldContain("> [!source]- URL — [Bookmarked](https://example.com/p)");
        md.ShouldContain("> ![](https://cdn/thumb.jpg)");
        md.ShouldContain("card blurb");
    }

    private static Attachment Binary(string mode, string kind, string filename, string mime)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new Attachment
        {
            Id = Guid.NewGuid(),
            NoteId = Guid.NewGuid(),
            ClientAttachmentId = "b",
            Kind = kind,
            Mode = mode,
            StorageProvider = "spaces",
            StorageBucket = "bucket",
            StorageKey = $"notes/x/{filename}",
            Filename = filename,
            MimeType = mime,
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = AttachmentExtractionStatus.Referenced,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
