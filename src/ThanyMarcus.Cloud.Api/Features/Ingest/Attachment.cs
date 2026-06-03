using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class Attachment : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid NoteId { get; init; }
    public string ClientAttachmentId { get; init; } = null!;

    public string Kind { get; init; } = null!;
    public string Mode { get; set; } = AttachmentMode.Extract;
    public string StorageProvider { get; init; } = null!;
    public string StorageBucket { get; init; } = null!;
    public string StorageKey { get; init; } = null!;

    public long? ByteSize { get; set; }
    public string? MimeType { get; set; }
    public string? Sha256 { get; set; }
    public string? Filename { get; init; }

    public string Status { get; set; } = AttachmentStatus.Pending;
    public string ExtractionStatus { get; set; } = AttachmentExtractionStatus.Pending;
    public string? ExtractedText { get; set; }
    public string? ExtractionError { get; set; }

    public JsonDocument Extra { get; set; } = null!;

    public Guid? ParentAttachmentId { get; set; }
    public string? ExtractionCacheKey { get; set; }
    public string? Url { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class AttachmentKind
{
    public const string Url = "url";
    public const string Image = "image";
    public const string Voice = "voice";
    public const string File = "file";

    public static bool IsValid(string kind) =>
        kind is Url or Image or Voice or File;

    public static bool IsBinary(string kind) =>
        kind is Image or Voice or File;
}

public static class AttachmentMode
{
    public const string Extract = "extract";
    public const string Reference = "reference";
    public const string Metadata = "metadata";

    public static bool IsValid(string mode) =>
        mode is Extract or Reference or Metadata;

    public static bool IsValidFor(string mode, string kind) => mode switch
    {
        Extract => true,
        Reference => true,
        Metadata => kind == AttachmentKind.Url,
        _ => false,
    };
}

public static class AttachmentStatus
{
    public const string Pending = "pending";
    public const string AwaitingUpload = "awaiting_upload";
    public const string Uploaded = "uploaded";
}

public static class AttachmentExtractionStatus
{
    public const string Pending = "pending";
    public const string Extracted = "extracted";
    public const string ExtractedMinimal = "extracted_minimal";
    public const string Skipped = "skipped";
    public const string Referenced = "referenced";
    public const string Failed = "failed";
}
