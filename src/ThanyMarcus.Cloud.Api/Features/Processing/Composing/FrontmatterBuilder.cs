using System.Globalization;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

public static class FrontmatterBuilder
{
    public static string Build(
        Note note,
        IReadOnlyList<Attachment> topLevelAttachments,
        string composeTemplate)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(topLevelAttachments);
        ArgumentNullException.ThrowIfNull(composeTemplate);

        var dto = new FrontmatterDto
        {
            Id              = note.Id.ToString(),
            CapturedAt      = note.CapturedAt.ToString("uuuu-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Modality        = "composite",
            AttachmentKinds = topLevelAttachments
                                .Select(a => a.Kind)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(k => k, StringComparer.Ordinal)
                                .ToList(),
            Source          = "plugin",
            LlmMode         = string.IsNullOrWhiteSpace(note.LlmMode) ? "safe" : note.LlmMode!,
            ComposeTemplate = composeTemplate,
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .DisableAliases()
            .Build();
        return serializer.Serialize(dto);
    }

    public static string BuildSynthesis(Note note, Instant updatedAt)
    {
        ArgumentNullException.ThrowIfNull(note);

        var dto = new SynthesisFrontmatterDto
        {
            ThanyNoteId    = note.Id.ToString(),
            ThanyUpdatedAt = updatedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ThanyLocked    = true,
            CssClasses     = new List<string> { "thany" },
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .DisableAliases()
            .Build();
        return serializer.Serialize(dto);
    }

    private sealed class FrontmatterDto
    {
        public string Id { get; set; } = null!;
        public string CapturedAt { get; set; } = null!;
        public string Modality { get; set; } = null!;
        public List<string> AttachmentKinds { get; set; } = new();
        public string Source { get; set; } = null!;
        public string LlmMode { get; set; } = null!;
        public string ComposeTemplate { get; set; } = null!;
    }

    private sealed class SynthesisFrontmatterDto
    {
        public string ThanyNoteId { get; set; } = null!;
        public string ThanyUpdatedAt { get; set; } = null!;
        public bool ThanyLocked { get; set; }

        [YamlMember(Alias = "cssclasses")]
        public List<string> CssClasses { get; set; } = new();
    }
}

public sealed record SynthesisFrontmatterFields(
    string PrivacyMode,
    string Model,
    string Preset,
    string PromptVersion,
    int Seed,
    Instant SynthesizedAt,
    string Status,
    string? Error);
