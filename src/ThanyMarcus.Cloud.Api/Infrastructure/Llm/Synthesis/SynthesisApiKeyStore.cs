using System.Collections.Concurrent;
using NodaTime;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

/// <summary>
/// In-memory, never-persisted store for per-note synthesis API keys.
/// IngestInit deposits the key here; the synthesis phase retrieves and consumes it.
/// If the worker restarts before synthesis runs the key is lost — the user re-submits.
/// Keys are intentionally NOT written to disk so a cloud admin with filesystem access cannot snoop.
/// </summary>
public interface ISynthesisApiKeyStore
{
    void Put(Guid noteId, string apiKey);
    string? Take(Guid noteId);
}

public sealed class InMemorySynthesisApiKeyStore : ISynthesisApiKeyStore
{
    private static readonly Duration Ttl = Duration.FromHours(24);

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly IClock clock;

    public InMemorySynthesisApiKeyStore(IClock clock)
    {
        this.clock = clock;
    }

    public void Put(Guid noteId, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return;
        Sweep();
        entries[noteId] = new Entry(apiKey, clock.GetCurrentInstant().Plus(Ttl));
    }

    public string? Take(Guid noteId)
    {
        Sweep();
        if (entries.TryRemove(noteId, out var entry))
        {
            if (entry.ExpiresAt < clock.GetCurrentInstant()) return null;
            return entry.ApiKey;
        }
        return null;
    }

    private void Sweep()
    {
        var now = clock.GetCurrentInstant();
        foreach (var kv in entries)
        {
            if (kv.Value.ExpiresAt < now) entries.TryRemove(kv.Key, out _);
        }
    }

    private sealed record Entry(string ApiKey, Instant ExpiresAt);
}
