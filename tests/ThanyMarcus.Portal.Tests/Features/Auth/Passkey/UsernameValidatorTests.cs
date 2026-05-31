using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Passkey;

public sealed class UsernameValidatorTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("bob_smith")]
    [InlineData("x_1")]
    [InlineData("AbC-123")]
    [InlineData("a-b")]
    public void Accepts_valid_usernames(string input)
    {
        var result = UsernameValidator.Validate(input);
        result.IsOk.ShouldBeTrue();
        result.Username.ShouldBe(input);
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        var result = UsernameValidator.Validate("  alice  ");
        result.IsOk.ShouldBeTrue();
        result.Username.ShouldBe("alice");
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("ab", "length")]
    [InlineData("", "length")]
    [InlineData("verylonglonglonglonglonglongusername", "length")]
    [InlineData("alice@bob", "chars")]
    [InlineData("has space", "chars")]
    [InlineData("ünïcode", "chars")]
    [InlineData("_alice", "prefix")]
    [InlineData("-alice", "prefix")]
    [InlineData("admin", "reserved")]
    [InlineData("ADMIN", "reserved")]
    [InlineData("thany", "reserved")]
    [InlineData("support", "reserved")]
    public void Rejects_with_expected_reason(string? input, string reason)
    {
        var result = UsernameValidator.Validate(input);
        result.IsOk.ShouldBeFalse();
        result.Reason.ShouldBe(reason);
    }

    [Fact]
    public void Length_is_checked_before_charset()
    {
        // "ab" is both too short and otherwise valid chars — length wins.
        UsernameValidator.Validate("ab").Reason.ShouldBe("length");
    }
}
