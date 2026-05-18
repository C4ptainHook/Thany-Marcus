using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class IngestJob : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid NoteId { get; init; }
    public string Kind { get; set; } = IngestJobKind.Capture;
    public string Status { get; set; } = IngestJobStatus.Queued;
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

public static class IngestJobStatus
{
    // 'processing' is legacy CLOUD-002; saga-rewrite ticket removes it.
    public const string Processing = "processing";
    public const string Queued = "queued";
    public const string ExtractingAttachments = "extracting_attachments";
    public const string Composing = "composing";
    public const string Routing = "routing";
    public const string ExtractingEntities = "extracting_entities";
    public const string Embedding = "embedding";
    public const string Succeeded = "succeeded";
    public const string FailedExtraction = "failed_extraction";
    public const string FailedComposition = "failed_composition";
    public const string FailedRoute = "failed_route";
    public const string FailedEntities = "failed_entities";
    public const string FailedEmbedding = "failed_embedding";
    public const string DeadLettered = "dead_lettered";
}

public static class IngestJobKind
{
    public const string Capture = "capture";
    public const string Reprocess = "reprocess";
    public const string HubRegen = "hub_regen";
}
