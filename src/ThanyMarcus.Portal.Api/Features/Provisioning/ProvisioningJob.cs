using System.Text.Json;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class ProvisioningJob : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CloudId { get; init; }
    public string Kind { get; init; } = null!;
    public JsonDocument Payload { get; init; } = null!;
    public string Status { get; set; } = null!;
    public int Attempts { get; set; }
    public string? WorkerId { get; set; }
    public Instant? LeaseExpires { get; set; }
    public string? LastError { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
