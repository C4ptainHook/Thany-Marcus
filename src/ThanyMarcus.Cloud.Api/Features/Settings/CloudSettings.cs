using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Settings;

public sealed class CloudSettings : IHasUpdatedAt
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public string LlmMode { get; set; } = LlmModes.Safe;
    public byte[]? EncryptedExternalApiKey { get; set; }
    public string? LlmModel { get; set; }

    public double? RelatedNotesMaxDistanceAuto { get; set; }
    public int? RelatedNotesAutoNoteCount { get; set; }
    public int? RelatedNotesAutoEntityCount { get; set; }

    public Instant? BootstrapConsumedAt { get; set; }
    public byte[]? RecoveryAnchorHash { get; set; }

    public bool ReindexInProgress { get; set; }

    public Instant UpdatedAt { get; set; }
}

public static class LlmModes
{
    public const string Safe = "safe";
    public const string UnsafeAnthropic = "unsafe_anthropic";
    public const string UnsafeOpenAi = "unsafe_openai";

    public static bool IsValid(string? mode) =>
        mode is Safe or UnsafeAnthropic or UnsafeOpenAi;
}
