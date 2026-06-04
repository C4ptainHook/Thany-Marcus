using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Tests.Features.Processing.Composing.Renderers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing;

public sealed class FrontmatterBuilderTests
{
    private static readonly string[] ExpectedKinds = { "image", "url", "voice" };

    [Fact]
    public void Emits_required_keys_in_documented_order()
    {
        var note = NewNote();
        var attachments = new[]
        {
            RendererTestSupport.NewAttachment(AttachmentKind.Voice),
            RendererTestSupport.NewAttachment(AttachmentKind.Url),
            RendererTestSupport.NewAttachment(AttachmentKind.Image, mime: "image/png"),
        };

        var yaml = FrontmatterBuilder.Build(note, attachments, "compose-v1");

        var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("id:");
        lines.First(l => l.StartsWith("captured_at:", StringComparison.Ordinal))
             .ShouldBe("captured_at: 2026-05-19T10:00:00.000Z");
        yaml.ShouldContain("modality: composite");
        yaml.ShouldContain("source: plugin");
        yaml.ShouldContain("llm_mode: safe");
        yaml.ShouldContain("compose_template: compose-v1");
    }

    [Fact]
    public void Attachment_kinds_are_distinct_and_sorted()
    {
        var note = NewNote();
        var attachments = new[]
        {
            RendererTestSupport.NewAttachment(AttachmentKind.Url),
            RendererTestSupport.NewAttachment(AttachmentKind.Image, mime: "image/png"),
            RendererTestSupport.NewAttachment(AttachmentKind.Voice),
            RendererTestSupport.NewAttachment(AttachmentKind.Url),
        };

        var yaml = FrontmatterBuilder.Build(note, attachments, "compose-v1");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var dto = deserializer.Deserialize<Probe>(yaml);
        dto.AttachmentKinds.ShouldBe(ExpectedKinds);
    }

    [Fact]
    public void Empty_attachment_list_emits_empty_list()
    {
        var note = NewNote();
        var yaml = FrontmatterBuilder.Build(note, Array.Empty<Attachment>(), "compose-v1");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var dto = deserializer.Deserialize<Probe>(yaml);
        dto.AttachmentKinds.ShouldBeEmpty();
    }

    [Fact]
    public void Result_is_round_trippable_yaml()
    {
        var note = NewNote();
        var yaml = FrontmatterBuilder.Build(note, Array.Empty<Attachment>(), "compose-v1");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var dto = deserializer.Deserialize<Probe>(yaml);
        dto.Id.ShouldBe(note.Id.ToString());
        dto.ComposeTemplate.ShouldBe("compose-v1");
        dto.Source.ShouldBe("plugin");
    }

    [Fact]
    public void Synthesis_frontmatter_emits_cssclasses_for_reading_skin_scope()
    {
        var note = NewNote();

        var yaml = FrontmatterBuilder.BuildSynthesis(note, Instant.FromUtc(2026, 5, 19, 10, 0));

        yaml.ShouldContain("thany_note_id:");
        yaml.ShouldContain("thany_locked: true");
        yaml.ShouldContain("cssclasses:");
        yaml.ShouldNotContain("css_classes:");
        yaml.ShouldContain("- thany");
    }

    private static Note NewNote() => new()
    {
        Id           = Guid.Parse("9b3c4f00-0000-0000-0000-000000000001"),
        ClientNoteId = "cn",
        CapturedAt   = Instant.FromUtc(2026, 5, 19, 10, 0),
        BodyInput    = "ignored",
        Status       = NoteStatus.Processing,
        LlmMode      = "safe",
        CreatedAt    = Instant.FromUtc(2026, 5, 19, 10, 0),
        UpdatedAt    = Instant.FromUtc(2026, 5, 19, 10, 0),
    };

    private sealed class Probe
    {
        public string Id { get; set; } = "";
        public string CapturedAt { get; set; } = "";
        public string Modality { get; set; } = "";
        public List<string> AttachmentKinds { get; set; } = new();
        public string Source { get; set; } = "";
        public string LlmMode { get; set; } = "";
        public string ComposeTemplate { get; set; } = "";
    }
}
