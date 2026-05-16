namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public interface ITurnstileValidator
{
    Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct);
}

public sealed record TurnstileVerifyResult(bool Success, IReadOnlyList<string> ErrorCodes)
{
    public static readonly TurnstileVerifyResult Disabled = new(true, []);
    public static readonly TurnstileVerifyResult Empty    = new(false, ["missing-input-response"]);
}
