using System.ComponentModel.DataAnnotations.Schema;
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
    public short ConsecutiveCrashes { get; set; }
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

    [NotMapped]
    public string? LastComposeTemplate { get; set; }
}

public static class IngestJobStatus
{
    public const string Queued = "queued";
    public const string ExtractingAttachments = "extracting_attachments";
    public const string ExtractingEntities = "extracting_entities";
    public const string Routing = "routing";
    public const string Synthesizing = "synthesizing";
    public const string Embedding = "embedding";
    public const string Succeeded = "succeeded";
    public const string FailedExtraction = "failed_extraction";
    public const string FailedEntities = "failed_entities";
    public const string FailedRoute = "failed_route";
    public const string FailedSynthesis = "failed_synthesis";
    public const string FailedEmbedding = "failed_embedding";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled";

    // Hub-regen flow uses Composing as its initial in-flight phase. Synthesis rework leaves this
    // for the hub pipeline only — user-ingest flow never enters Composing.
    public const string Composing = "composing";
    public const string FailedComposition = "failed_composition";

    public static readonly IReadOnlySet<string> Terminals = new HashSet<string>
    {
        Succeeded,
        FailedExtraction,
        FailedComposition,
        FailedEntities,
        FailedRoute,
        FailedSynthesis,
        FailedEmbedding,
        DeadLettered,
        Cancelled,
    };

    public static readonly IReadOnlySet<string> InFlightPhases = new HashSet<string>
    {
        ExtractingAttachments,
        ExtractingEntities,
        Routing,
        Synthesizing,
        Composing,
        Embedding,
    };

    public static bool IsTerminal(string status) => Terminals.Contains(status);

    public static string FailureTerminalFor(string phase) => phase switch
    {
        ExtractingAttachments => FailedExtraction,
        ExtractingEntities    => FailedEntities,
        Routing               => FailedRoute,
        Synthesizing          => FailedSynthesis,
        Composing             => FailedComposition,
        Embedding             => FailedEmbedding,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "no failure terminal mapping"),
    };
}

public static class IngestJobKind
{
    public const string Capture = "capture";
    public const string Reprocess = "reprocess";
    public const string HubRegen = "hub_regen";
    public const string UserEditEmbed = "user_edit_embed";
}
