using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public sealed record EntityExtractionDto(
    [property: JsonPropertyName("mentions")] IReadOnlyList<MentionCandidateDto> Mentions);

public sealed record MentionCandidateDto(
    [property: JsonPropertyName("anchor_text")] string AnchorText,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset,
    [property: JsonPropertyName("candidate_kind")] string CandidateKind,
    [property: JsonPropertyName("candidate_canonical")] string CandidateCanonical,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("confidence")] double Confidence);
