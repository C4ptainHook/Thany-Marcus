using System.Text.Json;
using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class EmergencyKit
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string HashedString { get; init; } = null!;     // Argon2id hash of the recovery string

    // Same DEK envelope shape as the passphrase wrap: a key is derived from the recovery
    // string via Argon2id, then the DEK is sealed under it with AES-GCM.
    public byte[] WrapArgon2Salt { get; init; } = null!;
    public JsonDocument WrapArgon2Params { get; init; } = null!;
    public byte[] WrappedDek { get; init; } = null!;
    public byte[] WrapNonce { get; init; } = null!;
    public byte[] WrapTag { get; init; } = null!;

    public Instant CreatedAt { get; init; }
    public Instant? UsedAt { get; set; }      // set when the kit is redeemed
    public Instant? RevokedAt { get; set; }   // set when superseded by a regeneration
}
