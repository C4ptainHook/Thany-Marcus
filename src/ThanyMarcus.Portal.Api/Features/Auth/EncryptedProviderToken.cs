using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class EncryptedProviderToken : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Provider { get; init; } = null!;

    public byte[] Ciphertext { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Tag { get; set; } = null!;

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
