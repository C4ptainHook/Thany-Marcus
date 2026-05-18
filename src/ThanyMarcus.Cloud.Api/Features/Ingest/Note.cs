using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class Note : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string? ClientNoteId { get; init; }
    public Instant CapturedAt { get; init; }
    public string Status { get; set; } = NoteStatus.Pending;
    public string BodyInput { get; init; } = null!;

    public string? RelativePath { get; set; }
    public string? BodyOutput { get; set; }
    public string? SuggestedProject { get; set; }
    public string[]? Tags { get; set; }
    public string? LlmMode { get; set; }
    public JsonDocument? Provenance { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class NoteStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";
}
