using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class User : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // Null for passkey-only ("sovereign") accounts that never went through Google SSO.
    public string? GoogleSubject { get; init; }
    public string? Email { get; set; }

    // Present for every account: backfilled from the email-local-part for Google users,
    // chosen at signup for passkey-only users. Case-insensitively unique. The generated
    // default is only a NOT-NULL fallback — both real creation paths set it explicitly.
    public string Username { get; set; } = "u_" + Guid.NewGuid().ToString("N")[..16];
    public string Name { get; set; } = null!;
    public string? ProfilePictureUrl { get; set; }
    public Instant? SessionsInvalidatedAt { get; set; }
    public Instant LastSeenAt { get; set; }

    public byte[]? PassphraseArgon2Salt { get; set; }
    public JsonDocument? PassphraseArgon2Params { get; set; }
    public byte[]? PassphraseWrappedDek { get; set; }
    public byte[]? PassphraseWrapNonce { get; set; }
    public byte[]? PassphraseWrapTag { get; set; }
    public Instant? PassphraseSetAt { get; set; }

    // DEK wrapped under a key derived from the TOTP shared secret. Present only while
    // TOTP is enabled; it is what makes the reset-via-TOTP recovery path possible.
    public byte[]? TotpWrappedDek { get; set; }
    public byte[]? TotpWrapNonce { get; set; }
    public byte[]? TotpWrapTag { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
