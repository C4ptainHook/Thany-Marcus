using System.Text.Json.Serialization;
using NodaTime;

namespace ThanyMarcus.Shared.PluginApi;

public sealed record SyncPullResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<SyncPullItem> Items,
    [property: JsonPropertyName("worker_state")] string WorkerState,
    [property: JsonPropertyName("server_timestamp")] Instant ServerTimestamp)
{
    public const string WorkerIdle     = "idle";
    public const string WorkerSpawning = "spawning";
    public const string WorkerWarm     = "warm";
}

public sealed record SyncPullItem(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("draft_id")] Guid DraftId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("processed_note")] ProcessedNote? ProcessedNote,
    [property: JsonPropertyName("error")] IngestError? Error,
    [property: JsonPropertyName("updated_at")] Instant UpdatedAt)
{
    public const string StatusProcessing = "processing";
    public const string StatusDone       = "done";
    public const string StatusFailed     = "failed";
}

public sealed record ProcessedNote(
    [property: JsonPropertyName("vault_path")] string VaultPath,
    [property: JsonPropertyName("frontmatter_yaml")] string FrontmatterYaml,
    [property: JsonPropertyName("body_markdown")] string BodyMarkdown,
    [property: JsonPropertyName("assets")] IReadOnlyList<ProcessedAsset> Assets);

public sealed record ProcessedAsset(
    [property: JsonPropertyName("vault_path")] string VaultPath,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);
