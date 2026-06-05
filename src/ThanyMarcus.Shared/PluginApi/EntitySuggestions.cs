using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record EntitySuggestionDto(
    [property: JsonPropertyName("id")]                Guid Id,
    [property: JsonPropertyName("canonicalText")]     string CanonicalText,
    [property: JsonPropertyName("kind")]              string Kind,
    [property: JsonPropertyName("aliases")]           IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("occurrenceCount")]   int OccurrenceCount,
    [property: JsonPropertyName("distinctNoteCount")] int DistinctNoteCount,
    [property: JsonPropertyName("firstSeenAt")]       DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")]        DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("sampleOccurrence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntitySuggestionOccurrenceDto? SampleOccurrence,
    [property: JsonPropertyName("suggestedMerge"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntitySuggestionMergeProposalDto? SuggestedMerge = null);

public sealed record EntitySuggestionOccurrenceDto(
    [property: JsonPropertyName("noteId")]          Guid NoteId,
    [property: JsonPropertyName("anchorText")]      string AnchorText,
    [property: JsonPropertyName("surroundingText")] string SurroundingText);

// A gray-zone cross-language merge proposal: this suggestion landed near an existing entity, near
// enough to likely be the same thing across languages but too far to auto-merge. The plugin offers
// "merge into <displayName>" alongside "accept as new".
public sealed record EntitySuggestionMergeProposalDto(
    [property: JsonPropertyName("entityId")]    Guid EntityId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("kind")]        string Kind,
    [property: JsonPropertyName("distance")]    double Distance);

public sealed record ListEntitySuggestionsResponse(
    [property: JsonPropertyName("suggestions")] IReadOnlyList<EntitySuggestionDto> Suggestions);

public sealed record AcceptEntitySuggestionResponse(
    [property: JsonPropertyName("entityId")] Guid EntityId);

public sealed record EditEntitySuggestionRequest(
    [property: JsonPropertyName("canonicalText")] string? CanonicalText,
    [property: JsonPropertyName("aliases")]       string[]? Aliases);

public sealed record EntitySuggestionPathConflict(
    [property: JsonPropertyName("conflict")]       string Conflict,
    [property: JsonPropertyName("existingNoteId")] Guid ExistingNoteId,
    [property: JsonPropertyName("existingKind")]   string ExistingKind);
