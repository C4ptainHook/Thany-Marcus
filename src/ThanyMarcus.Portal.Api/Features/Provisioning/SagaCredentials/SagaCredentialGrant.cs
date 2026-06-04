using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;

public sealed class SagaCredentialGrant
{
    public static readonly Duration Ttl = Duration.FromHours(6);

    public Guid CloudId { get; init; }
    public byte[] SealedDek { get; set; } = null!;
    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
}
