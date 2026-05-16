using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public sealed record ProviderTokenSummary(string Provider, Instant CreatedAt, Instant UpdatedAt);
