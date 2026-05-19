namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public interface IDigitalOceanOAuthClient
{
    Task<DoTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<DoTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct);
    Task RevokeOAuthTokenAsync(string accessToken, CancellationToken ct);
    Task<DoSpacesKeyMint> MintSpacesFullAccessKeyAsync(string accessToken, string name, CancellationToken ct);
    Task DeleteSpacesKeyAsync(string accessToken, string accessKeyId, CancellationToken ct);
}
