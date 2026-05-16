using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class CaptchaRequirementTrackerTests
{
    private static FakeClock NewClock() => new(Instant.FromUtc(2026, 5, 16, 12, 0));

    [Fact]
    public void MarkRequired_then_IsRequired_returns_true_within_ttl()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);

        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));

        tracker.IsRequired("u:abc").ShouldBeTrue();
    }

    [Fact]
    public void IsRequired_returns_false_when_no_entry_exists()
    {
        var tracker = new CaptchaRequirementTracker(NewClock());

        tracker.IsRequired("u:nobody").ShouldBeFalse();
    }

    [Fact]
    public void IsRequired_returns_false_after_ttl_expires_and_removes_entry()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);
        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));

        clock.Advance(Duration.FromMinutes(6));

        tracker.IsRequired("u:abc").ShouldBeFalse();
        tracker.Count.ShouldBe(0);
    }

    [Fact]
    public void IsRequired_treats_expiry_at_boundary_as_expired()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);
        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));

        clock.Advance(Duration.FromMinutes(5));

        tracker.IsRequired("u:abc").ShouldBeFalse();
    }

    [Fact]
    public void MarkRequired_picks_max_expiry_when_called_twice()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);

        tracker.MarkRequired("u:abc", Duration.FromMinutes(20));
        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));

        clock.Advance(Duration.FromMinutes(10));
        tracker.IsRequired("u:abc").ShouldBeTrue();
    }

    [Fact]
    public void MarkRequired_extends_when_later_call_has_larger_ttl()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);

        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));
        clock.Advance(Duration.FromMinutes(4));
        tracker.MarkRequired("u:abc", Duration.FromMinutes(20));

        clock.Advance(Duration.FromMinutes(10));
        tracker.IsRequired("u:abc").ShouldBeTrue();
    }

    [Fact]
    public void Clear_removes_entry_immediately()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);
        tracker.MarkRequired("u:abc", Duration.FromMinutes(5));

        tracker.Clear("u:abc");

        tracker.IsRequired("u:abc").ShouldBeFalse();
        tracker.Count.ShouldBe(0);
    }

    [Fact]
    public void Clear_is_noop_when_no_entry_exists()
    {
        var tracker = new CaptchaRequirementTracker(NewClock());

        tracker.Clear("u:nobody");

        tracker.Count.ShouldBe(0);
    }

    [Fact]
    public void Partitions_are_independent()
    {
        var clock = NewClock();
        var tracker = new CaptchaRequirementTracker(clock);
        tracker.MarkRequired("u:a", Duration.FromMinutes(5));
        tracker.MarkRequired("ip:1.2.3.4", Duration.FromMinutes(5));

        tracker.Clear("u:a");

        tracker.IsRequired("u:a").ShouldBeFalse();
        tracker.IsRequired("ip:1.2.3.4").ShouldBeTrue();
    }
}
