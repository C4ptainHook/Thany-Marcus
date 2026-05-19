using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

public sealed class FakeDigitalOceanOAuthClient : IDigitalOceanOAuthClient
{
    public List<string> ExchangedCodes { get; } = [];
    public List<string> RefreshedTokens { get; } = [];
    public List<string> RevokedTokens { get; } = [];
    public List<(string AccessToken, string Name)> MintCalls { get; } = [];
    public List<(string AccessToken, string AccessKeyId)> DeleteSpacesKeyCalls { get; } = [];

    public Func<string, DoTokenResponse>? OnExchange { get; set; }
    public Func<string, DoTokenResponse>? OnRefresh { get; set; }
    public Func<string, string, DoSpacesKeyMint>? OnMint { get; set; }

    public Task<DoTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        ExchangedCodes.Add(code);
        var handler = OnExchange ?? (_ => new DoTokenResponse
        {
            AccessToken  = "access-stub",
            RefreshToken = "refresh-stub",
            ExpiresIn    = 2592000,
        });
        return Task.FromResult(handler(code));
    }

    public Task<DoTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        RefreshedTokens.Add(refreshToken);
        var handler = OnRefresh ?? (_ => new DoTokenResponse
        {
            AccessToken  = "new-access",
            RefreshToken = "new-refresh",
            ExpiresIn    = 2592000,
        });
        return Task.FromResult(handler(refreshToken));
    }

    public Task RevokeOAuthTokenAsync(string accessToken, CancellationToken ct)
    {
        RevokedTokens.Add(accessToken);
        return Task.CompletedTask;
    }

    public Task<DoSpacesKeyMint> MintSpacesFullAccessKeyAsync(string accessToken, string name, CancellationToken ct)
    {
        MintCalls.Add((accessToken, name));
        var handler = OnMint ?? ((_, _) => new DoSpacesKeyMint
        {
            AccessKeyId = $"AKIA-{Guid.NewGuid():N}",
            SecretKey   = $"secret-{Guid.NewGuid():N}",
        });
        return Task.FromResult(handler(accessToken, name));
    }

    public Task DeleteSpacesKeyAsync(string accessToken, string accessKeyId, CancellationToken ct)
    {
        DeleteSpacesKeyCalls.Add((accessToken, accessKeyId));
        return Task.CompletedTask;
    }
}
