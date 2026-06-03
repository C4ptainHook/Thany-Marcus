using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record SyncReviveResponse(
    [property: JsonPropertyName("revived")] bool Revived);

public sealed record SyncStatusResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<SyncStatusItem> Items);

public sealed record SyncStatusItem(
    [property: JsonPropertyName("noteId")] Guid NoteId,
    [property: JsonPropertyName("status")] string Status);

public sealed record SyncDesiredResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<SyncDesiredItem> Items);

public sealed record SyncDesiredItem(
    [property: JsonPropertyName("noteId")]       Guid NoteId,
    [property: JsonPropertyName("relativePath")] string RelativePath);

public sealed record RegenerateHubResponse(
    [property: JsonPropertyName("hubNoteId")] Guid HubNoteId);

public sealed record SyncMoveRequest(
    [property: JsonPropertyName("relativePath")] string RelativePath);

public sealed record FolderDissolveRequest(
    [property: JsonPropertyName("folder")]       string Folder,
    [property: JsonPropertyName("mode")]         string Mode,
    [property: JsonPropertyName("targetFolder")] string? TargetFolder = null);

public sealed record FolderDissolveResponse(
    [property: JsonPropertyName("mode")]          string Mode,
    [property: JsonPropertyName("affectedCount")] int AffectedCount,
    [property: JsonPropertyName("desired")]       IReadOnlyList<SyncDesiredItem> Desired);

public sealed record FolderRef(
    [property: JsonPropertyName("folder")] string Folder);

public sealed record FolderListResponse(
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders);

public static class FolderDissolveMode
{
    public const string Reroute = "reroute";
    public const string ForceDelete = "force_delete";

    public static bool IsValid(string mode) => mode is Reroute or ForceDelete;
}
