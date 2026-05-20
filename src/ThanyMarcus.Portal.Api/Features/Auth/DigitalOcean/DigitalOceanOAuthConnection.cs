using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed class DigitalOceanOAuthConnection : IHasUpdatedAt
{
    public Guid UserId { get; init; }

    public byte[] AccessCiphertext { get; set; } = null!;
    public byte[] AccessNonce { get; set; } = null!;
    public byte[] AccessTag { get; set; } = null!;
    public Instant AccessExpiresAt { get; set; }

    public byte[] RefreshCiphertext { get; set; } = null!;
    public byte[] RefreshNonce { get; set; } = null!;
    public byte[] RefreshTag { get; set; } = null!;

    public string ConnectionStatus { get; set; } = "connected";

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class DigitalOceanConnectionStatus
{
    public const string Connected   = "connected";
    public const string NeedsReauth = "needs_reauth";
}
