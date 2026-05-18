using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class IngestJob : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid NoteId { get; init; }
    public string Status { get; set; } = IngestJobStatus.Queued;
    public short Attempts { get; set; }
    public string? LastError { get; set; }
    public string? LeaseOwner { get; set; }
    public Instant? LeaseExpiresAt { get; set; }
    public Instant ScheduledAt { get; set; }
    public Instant? StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class IngestJobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string DeadLettered = "dead_lettered";
}
