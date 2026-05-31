namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public sealed class PasskeyConfiguration
{
    public string ServerDomain { get; init; } = "localhost";
    public string ServerName { get; init; } = "Thany-Marcus Portal";
    public string[] Origins { get; init; } = [];
    public int ChallengeTtlSeconds { get; init; } = 300;
}
