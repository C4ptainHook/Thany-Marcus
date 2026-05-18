using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class PluginTokenMetadata : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CloudId { get; init; }
    public string Name { get; init; } = null!;
    public byte[] TokenHash { get; init; } = [];
    public Instant? LastUsedAt { get; set; }
    public Instant? RevokedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
