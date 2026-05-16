namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public sealed record TurnstileOptions
{
    public string SiteKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string SiteVerifyUrl { get; init; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    public int FailureThreshold { get; init; } = 5;
    public int TrackerTtlSeconds { get; init; } = 900;
    public int TimeoutSeconds { get; init; } = 5;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
