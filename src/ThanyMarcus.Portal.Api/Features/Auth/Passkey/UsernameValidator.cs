namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public readonly record struct UsernameValidationResult(bool IsOk, string Reason, string Username)
{
    public static UsernameValidationResult Ok(string username) => new(true, "", username);
    public static UsernameValidationResult Invalid(string reason) => new(false, reason, "");
}

public static class UsernameValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    private static readonly HashSet<string> Denylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "root", "support", "system", "api", "health", "oauth",
        "thany", "marcus", "help", "contact", "postmaster", "webmaster",
    };

    public static UsernameValidationResult Validate(string? raw)
    {
        if (raw is null) return UsernameValidationResult.Invalid("required");
        var trimmed = raw.Trim();
        if (trimmed.Length is < MinLength or > MaxLength) return UsernameValidationResult.Invalid("length");
        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return UsernameValidationResult.Invalid("chars");
        if (trimmed[0] is '_' or '-') return UsernameValidationResult.Invalid("prefix");
        if (Denylist.Contains(trimmed)) return UsernameValidationResult.Invalid("reserved");
        return UsernameValidationResult.Ok(trimmed);
    }
}
