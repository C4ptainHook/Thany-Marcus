using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class TotpBackupCode
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string HashedCode { get; init; } = null!;
    public Instant? UsedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
