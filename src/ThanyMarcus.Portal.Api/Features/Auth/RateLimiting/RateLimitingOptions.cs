namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public sealed record RateLimitingOptions
{
    public WindowOptions TotpChallenge { get; init; } = new(PermitLimit: 5, WindowSeconds: 300);
    public WindowOptions Unlock { get; init; } = new(PermitLimit: 5, WindowSeconds: 300);
    public WindowOptions SignInGoogle { get; init; } = new(PermitLimit: 20, WindowSeconds: 60);
    public WindowOptions SignupPasskey { get; init; } = new(PermitLimit: 10, WindowSeconds: 3600);

    public sealed record WindowOptions(int PermitLimit, int WindowSeconds);
}
