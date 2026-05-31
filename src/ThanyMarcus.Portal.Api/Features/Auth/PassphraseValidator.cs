namespace ThanyMarcus.Portal.Api.Features.Auth;

public enum PassphraseValidationResult { Ok, TooShort, TooCommon }

public sealed class PassphraseValidator
{
    public const int MinLength = 8;

    private readonly HashSet<string> commonPasswords;

    public PassphraseValidator(IReadOnlyCollection<string> commonPasswords) =>
        this.commonPasswords = new HashSet<string>(commonPasswords, StringComparer.OrdinalIgnoreCase);

    public PassphraseValidationResult Validate(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinLength)
            return PassphraseValidationResult.TooShort;
        if (commonPasswords.Contains(passphrase))
            return PassphraseValidationResult.TooCommon;
        return PassphraseValidationResult.Ok;
    }
}
