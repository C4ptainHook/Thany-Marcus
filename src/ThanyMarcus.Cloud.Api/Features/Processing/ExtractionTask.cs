using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class ExtractionTask : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid IngestJobId { get; init; }
    public Guid AttachmentId { get; init; }
    public string TargetSidecar { get; set; } = null!;
    public string Status { get; set; } = ExtractionTaskStatus.Queued;
    public short Attempts { get; set; }
    public string? LastError { get; set; }
    public string? LeaseOwner { get; set; }
    public Instant? LeaseExpiresAt { get; set; }
    public Instant ScheduledAt { get; set; }
    public Instant? StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public JsonDocument EventsLog { get; set; } = JsonDocument.Parse("[]");
    public long TransitionVersion { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class ExtractionTaskStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

public static class ExtractionTaskSidecar
{
    public const string Ollama = "ollama";
    public const string Docling = "docling";
    public const string Parakeet = "parakeet";
    public const string Url = "url";
    public const string UrlMetadata = "url_metadata";
    public const string Video = "video";
}
