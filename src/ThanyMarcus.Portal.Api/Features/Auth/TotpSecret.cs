using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class TotpSecret : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }

    public byte[] Ciphertext { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Tag { get; set; } = null!;

    public Instant? EnabledAt { get; set; }
    public Instant? DisabledAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
