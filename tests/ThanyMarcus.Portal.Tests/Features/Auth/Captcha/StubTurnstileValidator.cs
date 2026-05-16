using System.Collections.Concurrent;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class StubTurnstileValidator : ITurnstileValidator
{
    public const string ValidToken = "stub-valid-token";
    public const string InvalidToken = "stub-invalid-token";

    private readonly ConcurrentQueue<TurnstileVerifyResult> _queued = new();

    public int CallCount { get; private set; }
    public List<string> SeenTokens { get; } = [];
    public List<string?> SeenRemoteIps { get; } = [];

    public void Enqueue(TurnstileVerifyResult result) => _queued.Enqueue(result);

    public Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
    {
        CallCount++;
        SeenTokens.Add(token);
        SeenRemoteIps.Add(remoteIp);

        if (_queued.TryDequeue(out var queued))
            return Task.FromResult(queued);

        var result = token switch
        {
            ValidToken   => new TurnstileVerifyResult(true, []),
            InvalidToken => new TurnstileVerifyResult(false, ["invalid-input-response"]),
            _            => new TurnstileVerifyResult(false, ["invalid-input-response"]),
        };
        return Task.FromResult(result);
    }
}
