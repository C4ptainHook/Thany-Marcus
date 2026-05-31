using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public sealed class PasskeyCredential : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }

    public byte[] CredentialId { get; init; } = null!;
    public byte[] PublicKey { get; init; } = null!;
    public long SignCount { get; set; }
    public Guid Aaguid { get; init; }
    public string? AuthenticatorName { get; init; }
    public string[] Transports { get; init; } = [];
    public bool BackedUp { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? LastUsedAt { get; set; }
    public Instant? RevokedAt { get; set; }
}
