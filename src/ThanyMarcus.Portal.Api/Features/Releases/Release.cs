using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Releases;

public sealed class Release : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Version { get; set; } = null!;
    public string Strategy { get; set; } = null!;
    public string ComposeYaml { get; set; } = null!;
    public JsonDocument ImageDigests { get; set; } = null!;
    public JsonDocument ModelTags { get; set; } = null!;
    public JsonDocument EnvOverlay { get; set; } = null!;
    public string SchemaMinFrom { get; set; } = null!;
    public string? Notes { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
