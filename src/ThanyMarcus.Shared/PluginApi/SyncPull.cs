using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record SyncPullResponse(
    [property: JsonPropertyName("items")]     IReadOnlyList<SyncPullItem> Items,
    [property: JsonPropertyName("nextSince")] DateTimeOffset? NextSince);

public sealed record SyncPullItem(
    [property: JsonPropertyName("noteId")]            Guid NoteId,
    [property: JsonPropertyName("relativePath")]      string RelativePath,
    [property: JsonPropertyName("body")]              string Body,
    [property: JsonPropertyName("suggestedProject")]  string? SuggestedProject,
    [property: JsonPropertyName("tags")]              IReadOnlyList<string> Tags,
    [property: JsonPropertyName("llmMode")]           string? LlmMode,
    [property: JsonPropertyName("attachments")]       IReadOnlyList<SyncPullAttachment> Attachments,
    [property: JsonPropertyName("updatedAt")]         DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("deleted")]           bool Deleted = false,
    [property: JsonPropertyName("provenance"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Provenance = null,
    [property: JsonPropertyName("status"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("deletedAt"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? DeletedAt = null,
    [property: JsonPropertyName("extractionFailures"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SyncPullExtractionFailure>? ExtractionFailures = null);

public sealed record SyncPullExtractionFailure(
    [property: JsonPropertyName("kind")]         string Kind,
    [property: JsonPropertyName("attachmentId")] Guid AttachmentId,
    [property: JsonPropertyName("reason")]       string Reason,
    [property: JsonPropertyName("soft")]         bool Soft);

public sealed record SyncPullAttachment(
    [property: JsonPropertyName("attachmentId")]         Guid AttachmentId,
    [property: JsonPropertyName("kind")]                 string Kind,
    [property: JsonPropertyName("filename")]             string? Filename,
    [property: JsonPropertyName("mimeType")]             string? MimeType,
    [property: JsonPropertyName("byteSize")]             long? ByteSize,
    [property: JsonPropertyName("sha256")]               string? Sha256,
    [property: JsonPropertyName("downloadUrl")]          string? DownloadUrl,
    [property: JsonPropertyName("downloadUrlExpiresAt")] DateTimeOffset? DownloadUrlExpiresAt,
    [property: JsonPropertyName("extra")]                JsonElement Extra);

