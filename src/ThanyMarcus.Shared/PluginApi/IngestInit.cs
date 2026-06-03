using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record IngestInitRequest(
    [property: JsonPropertyName("clientNoteId")]     string ClientNoteId,
    [property: JsonPropertyName("capturedAt")]       DateTimeOffset CapturedAt,
    [property: JsonPropertyName("body")]             string Body,
    [property: JsonPropertyName("attachments")]      IReadOnlyList<IngestInitAttachment> Attachments,
    [property: JsonPropertyName("privacyMode")]      string? PrivacyMode = null,
    [property: JsonPropertyName("publicModel"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicModel = null,
    [property: JsonPropertyName("llmApiKey"),       JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LlmApiKey = null,
    [property: JsonPropertyName("synthesisPreset")]  string? SynthesisPreset = null,
    [property: JsonPropertyName("customPrompt"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CustomPrompt = null);

public sealed record IngestInitAttachment(
    [property: JsonPropertyName("clientAttachmentId")] string ClientAttachmentId,
    [property: JsonPropertyName("kind")]               string Kind,
    [property: JsonPropertyName("mimeType")]           string? MimeType,
    [property: JsonPropertyName("byteSize")]           long? ByteSize,
    [property: JsonPropertyName("sha256")]             string? Sha256,
    [property: JsonPropertyName("filename")]           string? Filename,
    [property: JsonPropertyName("extra")]              JsonElement Extra,
    [property: JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null);

public sealed record IngestInitResponse(
    [property: JsonPropertyName("noteId")]  Guid NoteId,
    [property: JsonPropertyName("uploads")] IReadOnlyList<IngestInitUpload> Uploads);

public sealed record IngestInitUpload(
    [property: JsonPropertyName("clientAttachmentId")] string ClientAttachmentId,
    [property: JsonPropertyName("attachmentId")]       Guid AttachmentId,
    [property: JsonPropertyName("uploadUrl")]          string UploadUrl,
    [property: JsonPropertyName("requiredHeaders")]    IReadOnlyDictionary<string, string> RequiredHeaders,
    [property: JsonPropertyName("expiresAt")]          DateTimeOffset ExpiresAt);

public static class PrivacyModes
{
    public const string Private = "private";
    public const string Public  = "public";

    public static bool IsValid(string? mode) => mode is Private or Public;
}

public static class PublicSynthesisModels
{
    public const string GeminiFlashLite25 = "gemini-2.5-flash-lite";
    public const string GeminiFlash25     = "gemini-2.5-flash";
    public const string GeminiFlash35     = "gemini-3.5-flash";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        GeminiFlashLite25, GeminiFlash25, GeminiFlash35,
    };

    public static bool IsValid(string? model) => model is not null && All.Contains(model);

    public static string ProviderOf(string model) => model switch
    {
        GeminiFlashLite25 or GeminiFlash25 or GeminiFlash35 => "google",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "unknown model"),
    };
}

public static class SynthesisPresets
{
    public const string Zettelkasten = "zettelkasten";
    public const string Journal      = "journal";
    public const string Encyclopedic = "encyclopedic";
    public const string Technical    = "technical";
    public const string Custom       = "custom";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Zettelkasten, Journal, Encyclopedic, Technical, Custom,
    };

    public static bool IsValid(string? preset) => preset is not null && All.Contains(preset);
}
