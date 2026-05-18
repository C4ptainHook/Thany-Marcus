using NodaTime;

namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public sealed class PluginToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public byte[] TokenHash { get; init; } = null!;
    public string Label { get; set; } = null!;
    public Instant CreatedAt { get; init; }
    public Instant? RevokedAt { get; set; }
}
