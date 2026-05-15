using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class Cloud : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Name { get; set; } = null!;
    public string Provider { get; init; } = null!;
    public string Region { get; init; } = null!;
    public string Hostname { get; set; } = null!;

    public string ProvisioningStatus { get; set; } = null!;
    public Instant? PlanStartedAt { get; set; }
    public Instant? ApplyStartedAt { get; set; }
    public Instant? DnsStartedAt { get; set; }
    public Instant? CertStartedAt { get; set; }
    public Instant? AdminStartedAt { get; set; }
    public Instant? ProvisioningCompletedAt { get; set; }
    public string? ProvisioningError { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? DestroyedAt { get; set; }
}
