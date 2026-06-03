using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Status;

public sealed record CloudStatusResponse(
    Guid CloudId,
    string Hostname,
    string Provider,
    string Region,
    string ProvisioningStatus,
    Instant? SucceededAt,
    Instant? DestroyedAt,
    Instant? CancelRequestedAt,
    JobSummary? CurrentJob,
    IReadOnlyList<EventSummary> RecentEvents,
    decimal? PriceMonthlyUsd,
    decimal? PriceHourlyUsd,
    string? PriceCurrency,
    Instant? PricedAt);

public sealed record JobSummary(Guid JobId, string Kind, string Status);

public sealed record EventSummary(string Phase, string? Event, string? Error, string Timestamp);
