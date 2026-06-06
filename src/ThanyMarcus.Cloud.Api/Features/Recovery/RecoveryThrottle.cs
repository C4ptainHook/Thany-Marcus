using NodaTime;

namespace ThanyMarcus.Cloud.Api.Features.Recovery;

public sealed class RecoveryThrottle(IClock clock)
{
    private const int MaxAttemptsPerWindow = 10;
    private static readonly Duration Window = Duration.FromMinutes(15);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Attempt> attempts = new(StringComparer.Ordinal);

    public bool TryConsume(string key)
    {
        lock (gate)
        {
            var now = clock.GetCurrentInstant();
            if (!attempts.TryGetValue(key, out var a) || now - a.WindowStart > Window)
            {
                attempts[key] = new Attempt(1, now);
                return true;
            }
            if (a.Count >= MaxAttemptsPerWindow)
                return false;
            attempts[key] = a with { Count = a.Count + 1 };
            return true;
        }
    }

    public void Reset(string key)
    {
        lock (gate)
        {
            attempts.Remove(key);
        }
    }

    private readonly record struct Attempt(int Count, Instant WindowStart);
}
