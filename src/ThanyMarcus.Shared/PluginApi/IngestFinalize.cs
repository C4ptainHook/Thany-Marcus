using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record IngestFinalizeRequest(
    [property: JsonPropertyName("uploaded")] IReadOnlyList<IngestFinalizeUploadedAttachment> Uploaded);

public sealed record IngestFinalizeUploadedAttachment(
    [property: JsonPropertyName("attachmentId")] Guid AttachmentId,
    [property: JsonPropertyName("sha256")]       string Sha256,
    [property: JsonPropertyName("byteSize")]     long ByteSize);

public sealed record IngestFinalizeResponse(
    [property: JsonPropertyName("noteId")] Guid NoteId,
    [property: JsonPropertyName("status")] string Status);

public sealed record IngestFinalizeMismatchEntry(
    [property: JsonPropertyName("attachmentId")] Guid AttachmentId,
    [property: JsonPropertyName("reason")]       string Reason,
    [property: JsonPropertyName("expected")]     string? Expected,
    [property: JsonPropertyName("actual")]       string? Actual);
