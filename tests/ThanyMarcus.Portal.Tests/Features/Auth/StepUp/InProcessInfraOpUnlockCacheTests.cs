using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class InProcessInfraOpUnlockCacheTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 15, 12, 0));

    private static byte[] MakeDek(byte fill = 0x42)
    {
        var dek = new byte[32];
        Array.Fill(dek, fill);
        return dek;
    }

    [Fact]
    public void TryGet_after_Set_copies_the_dek_into_destination()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        var dek = MakeDek();
        cache.Set(userId, dek);

        var dest = new byte[32];
        var ok = cache.TryGet(userId, dest);

        ok.ShouldBeTrue();
        dest.ShouldBe(dek);
    }

    [Fact]
    public void TryGet_on_unknown_user_returns_false()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var dest = new byte[32];

        cache.TryGet(Guid.NewGuid(), dest).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_on_expired_entry_returns_false_and_removes_it()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        cache.Set(userId, MakeDek());

        _clock.Advance(Duration.FromMinutes(11));

        var dest = new byte[32];
        cache.TryGet(userId, dest).ShouldBeFalse();
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void TryGet_slides_the_ttl_forward_on_each_hit()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        cache.Set(userId, MakeDek());

        _clock.Advance(Duration.FromMinutes(5));
        var dest = new byte[32];
        cache.TryGet(userId, dest).ShouldBeTrue();

        _clock.Advance(Duration.FromMinutes(9));
        cache.TryGet(userId, dest).ShouldBeTrue();

        _clock.Advance(Duration.FromMinutes(11));
        cache.TryGet(userId, dest).ShouldBeFalse();
    }

    [Fact]
    public void Set_overwrites_and_zeros_the_previous_buffer()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        cache.Set(userId, MakeDek(0xAA));
        var previousBuffer = cache.PeekDekBufferReference(userId)!;

        cache.Set(userId, MakeDek(0xBB));

        previousBuffer.ShouldAllBe(b => b == 0);
        var dest = new byte[32];
        cache.TryGet(userId, dest).ShouldBeTrue();
        dest.ShouldAllBe(b => b == 0xBB);
    }

    [Fact]
    public void Invalidate_removes_and_zeros_buffer()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        cache.Set(userId, MakeDek(0xCC));
        var buffer = cache.PeekDekBufferReference(userId)!;

        cache.Invalidate(userId);

        cache.Count.ShouldBe(0);
        buffer.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void Invalidate_on_unknown_user_is_a_noop()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        Should.NotThrow(() => cache.Invalidate(Guid.NewGuid()));
    }

    [Fact]
    public void Concurrent_TryGet_is_safe()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userId = Guid.NewGuid();
        cache.Set(userId, MakeDek(0xAB));

        var hits = 0;
        Parallel.For(0, 100, _ =>
        {
            var dest = new byte[32];
            if (cache.TryGet(userId, dest))
                Interlocked.Increment(ref hits);
        });

        hits.ShouldBe(100);
        cache.Count.ShouldBe(1);
    }

    [Fact]
    public void Stress_concurrent_Set_TryGet_Invalidate_does_not_corrupt_reads()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var userIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var patterns = new[] { MakeDek(0xAA), MakeDek(0xBB), MakeDek(0xCC) };

        var corrupted = 0;
        Parallel.For(0, 20_000, i =>
        {
            var uid = userIds[i % userIds.Length];
            switch (i % 5)
            {
                case 0:
                case 1:
                    cache.Set(uid, patterns[i % patterns.Length]);
                    break;
                case 2:
                case 3:
                    var dest = new byte[32];
                    if (cache.TryGet(uid, dest))
                    {
                        var matched = false;
                        foreach (var p in patterns)
                        {
                            if (p.AsSpan().SequenceEqual(dest)) { matched = true; break; }
                        }
                        if (!matched) Interlocked.Increment(ref corrupted);
                    }
                    break;
                case 4:
                    cache.Invalidate(uid);
                    break;
            }
        });

        corrupted.ShouldBe(0);
    }

    [Fact]
    public void SweepExpired_removes_expired_entries_and_keeps_fresh_ones()
    {
        var cache = new InProcessInfraOpUnlockCache(_clock);
        var stale = Guid.NewGuid();
        cache.Set(stale, MakeDek(0x01));

        _clock.Advance(Duration.FromMinutes(9));
        var fresh = Guid.NewGuid();
        cache.Set(fresh, MakeDek(0x02));

        _clock.Advance(Duration.FromMinutes(2));

        cache.SweepExpired();

        cache.Count.ShouldBe(1);
        var dest = new byte[32];
        cache.TryGet(fresh, dest).ShouldBeTrue();
        cache.TryGet(stale, dest).ShouldBeFalse();
    }
}
