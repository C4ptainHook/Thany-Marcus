namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public sealed record TurnstileKindMetadata(string Kind);

public static class TurnstileKinds
{
    public const string Signin = "signin";
}
