using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public enum PasskeyChallengeKind { Register, Login, Signup }

public sealed record PasskeyChallengeRecord(
    string OptionsJson,
    Guid? UserId,
    PasskeyChallengeKind Kind,
    Instant ExpiresAt,
    string? Username = null);

public interface IPasskeyChallengeStore
{
    Guid Stash(string optionsJson, Guid? userId, PasskeyChallengeKind kind, string? username = null);

    /// <summary>Single-use: removes the entry on read. Returns null if missing or expired.</summary>
    PasskeyChallengeRecord? Take(Guid challengeId);

    int SweepExpired();
}

public sealed class PasskeyChallengeStore(IClock clock, IOptions<PasskeyConfiguration> options)
    : IPasskeyChallengeStore
{
    private readonly ConcurrentDictionary<Guid, PasskeyChallengeRecord> entries = new();

    public Guid Stash(string optionsJson, Guid? userId, PasskeyChallengeKind kind, string? username = null)
    {
        var id = Guid.NewGuid();
        var expiresAt = clock.GetCurrentInstant() + Duration.FromSeconds(options.Value.ChallengeTtlSeconds);
        entries[id] = new PasskeyChallengeRecord(optionsJson, userId, kind, expiresAt, username);
        return id;
    }

    public PasskeyChallengeRecord? Take(Guid challengeId)
    {
        if (!entries.TryRemove(challengeId, out var record))
            return null;
        return record.ExpiresAt < clock.GetCurrentInstant() ? null : record;
    }

    public int SweepExpired()
    {
        var now = clock.GetCurrentInstant();
        var removed = 0;
        foreach (var (id, record) in entries)
        {
            if (record.ExpiresAt < now && entries.TryRemove(id, out _))
                removed++;
        }
        return removed;
    }
}

public sealed class PasskeyChallengeStoreSweeper(IPasskeyChallengeStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                store.SweepExpired();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
