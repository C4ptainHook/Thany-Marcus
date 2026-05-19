using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public sealed record PreflightResult(
    bool ShouldExtract,
    string? SkipReason,
    JsonDocument? Metadata)
{
    public static PreflightResult Pass { get; } = new(true, null, null);

    public static PreflightResult PassWith(JsonDocument meta) => new(true, null, meta);

    public static PreflightResult Skip(string reason) => new(false, reason, null);

    public static PreflightResult Skip(string reason, JsonDocument meta) => new(false, reason, meta);
}
