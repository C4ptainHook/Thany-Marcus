using System.Text.Json;
using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class RecoveryCode
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string HashedCode { get; init; } = null!;

    public byte[] WrapArgon2Salt { get; init; } = null!;
    public JsonDocument WrapArgon2Params { get; init; } = null!;
    public byte[] WrappedDek { get; init; } = null!;
    public byte[] WrapNonce { get; init; } = null!;
    public byte[] WrapTag { get; init; } = null!;

    public Instant? UsedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
