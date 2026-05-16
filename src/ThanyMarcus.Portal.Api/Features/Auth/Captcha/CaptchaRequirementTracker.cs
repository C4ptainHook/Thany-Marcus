using System.Collections.Concurrent;
using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth.Captcha;

public sealed class CaptchaRequirementTracker(IClock clock)
{
    private readonly ConcurrentDictionary<string, Instant> _expiries = new();

    public void MarkRequired(string partitionKey, Duration ttl)
    {
        var expires = clock.GetCurrentInstant() + ttl;
        _expiries.AddOrUpdate(partitionKey, expires, (_, existing) => existing > expires ? existing : expires);
    }

    public bool IsRequired(string partitionKey)
    {
        if (!_expiries.TryGetValue(partitionKey, out var expires)) return false;
        if (expires <= clock.GetCurrentInstant())
        {
            _expiries.TryRemove(partitionKey, out _);
            return false;
        }
        return true;
    }

    public void Clear(string partitionKey) => _expiries.TryRemove(partitionKey, out _);

    internal int Count => _expiries.Count;
}
