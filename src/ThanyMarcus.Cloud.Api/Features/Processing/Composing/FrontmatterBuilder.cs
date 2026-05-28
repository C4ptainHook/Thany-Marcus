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

    public static string BuildSynthesis(
        Note note,
        IReadOnlyList<Attachment> topLevelAttachments,
        SynthesisFrontmatterFields synthesis)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(topLevelAttachments);
        ArgumentNullException.ThrowIfNull(synthesis);

        var dto = new SynthesisFrontmatterDto
        {
            ThanyNoteId       = note.Id.ToString(),
            ThanyUpdatedAt    = synthesis.SynthesizedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ThanyLocked       = true,
            SuggestedProject  = note.SuggestedProject,
            LlmMode           = string.IsNullOrWhiteSpace(note.LlmMode) ? null : note.LlmMode,
            Tags              = (note.Tags is { Length: > 0 }) ? note.Tags.ToList() : null,
            AttachmentKinds   = topLevelAttachments
                                    .Select(a => a.Kind)
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(k => k, StringComparer.Ordinal)
                                    .ToList(),
            PrivacyMode       = synthesis.PrivacyMode,
            SynthesisModel    = synthesis.Model,
            SynthesisPromptVersion = synthesis.PromptVersion,
            SynthesisSeed     = synthesis.Seed,
            SynthesizedAt     = synthesis.SynthesizedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            SynthesisStatus   = synthesis.Status,
            SynthesisError    = synthesis.Error,
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .DisableAliases()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
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
        public string? SuggestedProject { get; set; }
        public string? LlmMode { get; set; }
        public List<string>? Tags { get; set; }
        public List<string> AttachmentKinds { get; set; } = new();
        public string PrivacyMode { get; set; } = null!;
        public string SynthesisModel { get; set; } = null!;
        public string SynthesisPromptVersion { get; set; } = null!;
        public int SynthesisSeed { get; set; }
        public string SynthesizedAt { get; set; } = null!;
        public string SynthesisStatus { get; set; } = null!;
        public string? SynthesisError { get; set; }
    }
}

public sealed record SynthesisFrontmatterFields(
    string PrivacyMode,
    string Model,
    string PromptVersion,
    int Seed,
    Instant SynthesizedAt,
    string Status,
    string? Error);
