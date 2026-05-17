using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class User : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string GoogleSubject { get; init; } = null!;
    public string Email { get; set; } = null!;
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

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
