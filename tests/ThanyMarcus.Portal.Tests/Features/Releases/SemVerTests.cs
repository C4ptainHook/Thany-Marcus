using Shouldly;
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Portal.Tests.Features.Releases;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("10.20.30+build.5", 10, 20, 30)]
    public void Parses_core_versions(string input, int major, int minor, int patch)
    {
        SemVer.TryParse(input, out var v).ShouldBeTrue();
        v.Major.ShouldBe(major);
        v.Minor.ShouldBe(minor);
        v.Patch.ShouldBe(patch);
        v.PreRelease.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    public void Rejects_malformed_versions(string input) =>
        SemVer.TryParse(input, out _).ShouldBeFalse();

    [Fact]
    public void Orders_by_core_then_prerelease()
    {
        SemVer.TryParse("1.0.0", out var stable).ShouldBeTrue();
        SemVer.TryParse("1.0.0-rc.1", out var rc).ShouldBeTrue();
        SemVer.TryParse("1.0.1", out var patch).ShouldBeTrue();
        SemVer.TryParse("2.0.0", out var major).ShouldBeTrue();

        (rc < stable).ShouldBeTrue();
        (stable < patch).ShouldBeTrue();
        (patch < major).ShouldBeTrue();
    }

    [Fact]
    public void Prerelease_numeric_identifiers_compare_numerically()
    {
        SemVer.TryParse("1.0.0-rc.2", out var rc2).ShouldBeTrue();
        SemVer.TryParse("1.0.0-rc.10", out var rc10).ShouldBeTrue();
        (rc2 < rc10).ShouldBeTrue();
    }

    [Fact]
    public void Equal_versions_are_not_ordered()
    {
        SemVer.TryParse("3.4.5", out var a).ShouldBeTrue();
        SemVer.TryParse("3.4.5", out var b).ShouldBeTrue();
        a.CompareTo(b).ShouldBe(0);
        (a < b).ShouldBeFalse();
        (a > b).ShouldBeFalse();
    }
}
