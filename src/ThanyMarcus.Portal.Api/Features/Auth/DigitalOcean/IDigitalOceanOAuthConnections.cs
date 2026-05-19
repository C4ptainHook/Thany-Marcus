using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public interface IDigitalOceanOAuthConnections
{
    Task SaveAsync(Guid userId, string accessToken, string refreshToken, Instant accessExpiresAt,
                   ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<bool> IsConnectedAsync(Guid userId, CancellationToken ct);
    Task<DigitalOceanConnectionInfo?> GetInfoAsync(Guid userId, CancellationToken ct);
    Task<string?> GetAccessTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<string?> GetRefreshTokenAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task SetConnectionStatusAsync(Guid userId, string status, CancellationToken ct);
    Task DisconnectAsync(Guid userId, CancellationToken ct);
}

public sealed record DigitalOceanConnectionInfo(Instant AccessExpiresAt, string ConnectionStatus);
