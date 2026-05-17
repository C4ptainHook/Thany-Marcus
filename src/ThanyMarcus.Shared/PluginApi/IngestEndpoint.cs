using System.Text.Json.Serialization;
using NodaTime;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record IngestCompositeManifest(
    [property: JsonPropertyName("draft_id")] Guid DraftId,
    [property: JsonPropertyName("body_markdown")] string BodyMarkdown,
    [property: JsonPropertyName("parts")] IReadOnlyList<IngestCompositePart> Parts,
    [property: JsonPropertyName("client_timestamp")] Instant ClientTimestamp,
    [property: JsonPropertyName("vault_hint")] string? VaultHint);

public sealed record IngestCompositePart(
    [property: JsonPropertyName("part_id")] string PartId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("inline_value")] string? InlineValue,
    [property: JsonPropertyName("multipart_name")] string? MultipartName,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("captured_at")] Instant? CapturedAt)
{
    public const string KindUrl   = "url";
    public const string KindText  = "text";
    public const string KindImage = "image";
    public const string KindAudio = "audio";
    public const string KindPdf   = "pdf";
}

public sealed record IngestResponse(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("draft_id")] Guid DraftId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("received_at")] Instant ReceivedAt)
{
    public const string StatusAccepted = "accepted";
}

public sealed record IngestError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("part_id")] string? PartId)
{
    public const string CodeProcessorFailed    = "processor_failed";
    public const string CodeWorkerUnavailable  = "worker_unavailable";
    public const string CodeInvalidManifest    = "invalid_manifest";
    public const string CodeUnsupportedKind    = "unsupported_kind";
    public const string CodePayloadTooLarge    = "payload_too_large";
}
