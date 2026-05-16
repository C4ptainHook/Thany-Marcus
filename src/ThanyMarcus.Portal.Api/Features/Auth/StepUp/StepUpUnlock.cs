using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class StepUpUnlock
{
    public Guid UserId { get; init; }
    public byte[] EncryptedDek { get; set; } = null!;
    public Instant ExpiresAt { get; set; }
    public Instant LastUsedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
