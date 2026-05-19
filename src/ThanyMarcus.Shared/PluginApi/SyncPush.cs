using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record SyncPushRequest(
    [property: JsonPropertyName("noteId")]         Guid NoteId,
    [property: JsonPropertyName("body")]           string Body,
    [property: JsonPropertyName("baseUpdatedAt")]  DateTimeOffset BaseUpdatedAt,
    [property: JsonPropertyName("deleted")]        bool Deleted = false);

public sealed record SyncPushResponse(
    [property: JsonPropertyName("noteId")]            Guid NoteId,
    [property: JsonPropertyName("updatedAt")]         DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("transitionVersion")] long TransitionVersion);

public sealed record SyncPushConflict(
    [property: JsonPropertyName("code")]                     string Code,
    [property: JsonPropertyName("currentUpdatedAt")]         DateTimeOffset CurrentUpdatedAt,
    [property: JsonPropertyName("currentTransitionVersion")] long CurrentTransitionVersion);

public static class SyncPushConflictCodes
{
    public const string StaleBaseline = "stale_baseline";
    public const string NoteNotFound  = "note_not_found";
}
