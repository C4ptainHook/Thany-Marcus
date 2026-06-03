using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using NodaTime;
using Pgvector;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class Note : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string? ClientNoteId { get; init; }
    public Instant CapturedAt { get; init; }
    public string Status { get; set; } = NoteStatus.Pending;
    public string Kind { get; set; } = NoteKind.SynthNote;
    public string BodyInput { get; init; } = null!;

    public string? RelativePath { get; set; }
    public string? BodyOutput { get; set; }
    public string[]? Tags { get; set; }
    public string? LlmMode { get; set; }
    public JsonDocument? Provenance { get; set; }

    public Vector? Embedding { get; set; }
    public string? BodyHash { get; set; }
    public Instant? DeletedAt { get; set; }
    public bool IsHub { get; set; }
    public Guid? HubEntityId { get; set; }
    public long TransitionVersion { get; set; }

    public string? PrivacyMode { get; set; }
    public string? PublicModel { get; set; }
    public string? SynthesisPreset { get; set; }
    public string? SynthesisPromptBody { get; set; }
    public string? SynthesisCacheKey { get; set; }
    public string? SynthesisCacheValue { get; set; }

    [NotMapped]
    public string? SuggestedProject { get; set; }

    [NotMapped]
    public string? LlmApiKey { get; set; }

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

public static class NoteKind
{
    public const string SynthNote = "synth_note";
    public const string EntityStub = "entity_stub";
}
