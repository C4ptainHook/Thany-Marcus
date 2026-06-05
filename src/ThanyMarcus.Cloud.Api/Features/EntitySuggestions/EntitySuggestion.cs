using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using Pgvector;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public sealed class EntitySuggestion
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string CanonicalText { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string[] Aliases { get; set; } = [];
    public JsonDocument Occurrences { get; set; } = JsonDocument.Parse("[]");
    public int OccurrenceCount { get; set; }
    public int DistinctNoteCount { get; set; }
    public Vector? Embedding { get; set; }
    public Instant FirstSeenAt { get; set; }
    public Instant LastSeenAt { get; set; }
    public Instant? AcceptedAt { get; set; }
    public Guid? AcceptedEntityId { get; set; }
    public Guid? SuggestedMergeEntityId { get; set; }
    public double? SuggestedMergeDistance { get; set; }
    public Instant? DismissedAt { get; set; }
    public Instant CreatedAt { get; init; }
}

public sealed record EntitySuggestionOccurrence(
    [property: JsonPropertyName("note_id")]          Guid NoteId,
    [property: JsonPropertyName("anchor_text")]      string AnchorText,
    [property: JsonPropertyName("surrounding_text")] string SurroundingText,
    [property: JsonPropertyName("observed_at")]      DateTimeOffset ObservedAt);

public static class EntitySuggestionOccurrences
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static List<EntitySuggestionOccurrence> Parse(JsonDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.Deserialize<List<EntitySuggestionOccurrence>>(Opts) ?? new();
    }

    public static JsonDocument Serialize(IReadOnlyList<EntitySuggestionOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        return JsonDocument.Parse(JsonSerializer.Serialize(occurrences, Opts));
    }
}
