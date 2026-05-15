using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class AuthLockout
{
    public Guid UserId { get; init; }
    public string Kind { get; init; } = null!;
    public short FailedCount { get; set; }
    public Instant? LockedUntil { get; set; }
    public Instant LastAttemptAt { get; set; }
}
