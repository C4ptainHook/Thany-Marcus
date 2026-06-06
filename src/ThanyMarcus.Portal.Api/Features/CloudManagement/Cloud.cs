using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement;

public sealed class Cloud : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Name { get; set; } = null!;
    public string Provider { get; init; } = null!;
    public string Region { get; init; } = null!;
    public string Hostname { get; set; } = null!;

    public string? Subdomain { get; set; }
    public string? VmIp { get; set; }
    public string? TerraformWorkspace { get; set; }
    public Guid? ProviderTokenId { get; set; }

    public string ProvisioningStatus { get; set; } = null!;
    public Instant? MintingSpacesStartedAt { get; set; }
    public Instant? PlanStartedAt { get; set; }
    public Instant? ApplyStartedAt { get; set; }
    public Instant? DnsStartedAt { get; set; }
    public Instant? CertStartedAt { get; set; }
    public Instant? AdminStartedAt { get; set; }
    public Instant? ProvisioningCompletedAt { get; set; }
    public Instant? CancelRequestedAt { get; set; }
    public string? ProvisioningError { get; set; }

    public decimal? PriceMonthlyUsd { get; set; }
    public decimal? PriceHourlyUsd { get; set; }
    public string? PriceCurrency { get; set; }
    public Instant? PricedAt { get; set; }
    public string? PricedSource { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? DestroyedAt { get; set; }
}
