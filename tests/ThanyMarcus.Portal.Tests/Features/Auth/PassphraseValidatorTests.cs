using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class PassphraseValidatorTests
{
    private static readonly PassphraseValidator Validator = new(CommonPasswords.Load());

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1234567")]   // 7 chars — too short wins even though it is also common
    public void Rejects_passphrases_under_eight_characters(string passphrase) =>
        Validator.Validate(passphrase).ShouldBe(PassphraseValidationResult.TooShort);

    [Theory]
    [InlineData("password")]
    [InlineData("qwerty12")]
    [InlineData("12345678")]
    public void Rejects_breach_list_passphrases(string passphrase) =>
        Validator.Validate(passphrase).ShouldBe(PassphraseValidationResult.TooCommon);

    [Fact]
    public void Breach_check_is_case_insensitive() =>
        Validator.Validate("PASSWORD").ShouldBe(PassphraseValidationResult.TooCommon);

    [Theory]
    [InlineData("summit-roses-galaxy-7")]
    [InlineData("swift-river")]
    [InlineData("hunter2hunter2")]
    public void Accepts_long_uncommon_passphrases(string passphrase) =>
        Validator.Validate(passphrase).ShouldBe(PassphraseValidationResult.Ok);

    [Fact]
    public void Eight_character_floor_is_inclusive() =>
        Validator.Validate("8charsok").ShouldBe(PassphraseValidationResult.Ok);
}
