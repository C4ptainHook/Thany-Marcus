using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record IngestInitRequest(
    [property: JsonPropertyName("clientNoteId")]   string ClientNoteId,
    [property: JsonPropertyName("capturedAt")]     DateTimeOffset CapturedAt,
    [property: JsonPropertyName("body")]           string Body,
    [property: JsonPropertyName("attachments")]    IReadOnlyList<IngestInitAttachment> Attachments);

public sealed record IngestInitAttachment(
    [property: JsonPropertyName("clientAttachmentId")] string ClientAttachmentId,
    [property: JsonPropertyName("kind")]               string Kind,
    [property: JsonPropertyName("mimeType")]           string? MimeType,
    [property: JsonPropertyName("byteSize")]           long? ByteSize,
    [property: JsonPropertyName("sha256")]             string? Sha256,
    [property: JsonPropertyName("filename")]           string? Filename,
    [property: JsonPropertyName("extra")]              JsonElement Extra);

public sealed record IngestInitResponse(
    [property: JsonPropertyName("noteId")]  Guid NoteId,
    [property: JsonPropertyName("uploads")] IReadOnlyList<IngestInitUpload> Uploads);

public sealed record IngestInitUpload(
    [property: JsonPropertyName("clientAttachmentId")] string ClientAttachmentId,
    [property: JsonPropertyName("attachmentId")]       Guid AttachmentId,
    [property: JsonPropertyName("uploadUrl")]          string UploadUrl,
    [property: JsonPropertyName("requiredHeaders")]    IReadOnlyDictionary<string, string> RequiredHeaders,
    [property: JsonPropertyName("expiresAt")]          DateTimeOffset ExpiresAt);
