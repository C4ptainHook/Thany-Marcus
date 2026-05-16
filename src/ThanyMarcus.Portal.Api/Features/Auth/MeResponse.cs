namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string Name,
    string? ProfilePictureUrl,
    string Totp,
    bool PassphraseSet);
