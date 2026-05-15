using System.Security.Cryptography;
using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth.StepUp;

public sealed class InProcessInfraOpUnlockCache(IClock clock) : IInfraOpUnlockCache
{
    private static readonly Duration SlidingTtl = Duration.FromMinutes(10);
    private readonly Dictionary<Guid, CacheEntry> _entries = new();
    private readonly Lock _sync = new();

    public bool TryGet(Guid userId, Span<byte> dekDestination)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(userId, out var entry)) return false;
            var now = clock.GetCurrentInstant();
            if (entry.ExpiresAt <= now)
            {
                _entries.Remove(userId);
                CryptographicOperations.ZeroMemory(entry.Dek);
                return false;
            }
            entry.Dek.AsSpan().CopyTo(dekDestination);
            entry.ExpiresAt = now + SlidingTtl;
            return true;
        }
    }

    public void Set(Guid userId, ReadOnlySpan<byte> dek)
    {
        var copy = dek.ToArray();
        var expiresAt = clock.GetCurrentInstant() + SlidingTtl;
        lock (_sync)
        {
            if (_entries.Remove(userId, out var prev))
                CryptographicOperations.ZeroMemory(prev.Dek);
            _entries[userId] = new CacheEntry { Dek = copy, ExpiresAt = expiresAt };
        }
    }

    public void Invalidate(Guid userId)
    {
        lock (_sync)
        {
            if (_entries.Remove(userId, out var entry))
                CryptographicOperations.ZeroMemory(entry.Dek);
        }
    }

    internal void SweepExpired()
    {
        lock (_sync)
        {
            var now = clock.GetCurrentInstant();
            List<Guid>? expired = null;
            foreach (var (id, entry) in _entries)
            {
                if (entry.ExpiresAt <= now)
                {
                    expired ??= new List<Guid>();
                    expired.Add(id);
                }
            }
            if (expired is null) return;
            foreach (var id in expired)
            {
                if (_entries.Remove(id, out var entry))
                    CryptographicOperations.ZeroMemory(entry.Dek);
            }
        }
    }

    internal int Count
    {
        get { lock (_sync) { return _entries.Count; } }
    }

    internal byte[]? PeekDekBufferReference(Guid userId)
    {
        lock (_sync) { return _entries.TryGetValue(userId, out var entry) ? entry.Dek : null; }
    }

    private sealed class CacheEntry
    {
        public required byte[] Dek { get; init; }
        public Instant ExpiresAt { get; set; }
    }
}
