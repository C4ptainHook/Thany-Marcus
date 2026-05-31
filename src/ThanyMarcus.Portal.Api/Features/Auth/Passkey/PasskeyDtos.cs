using Fido2NetLib;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public sealed record PasskeyRegisterCompleteRequest(Guid ChallengeId, AuthenticatorAttestationRawResponse Response);

public sealed record PasskeyLoginCompleteRequest(Guid ChallengeId, AuthenticatorAssertionRawResponse Response);

public sealed record PasskeySignupChallengeRequest(
    string? Username,
    bool AcknowledgedNoRecovery,
    string? TurnstileToken);

public sealed record PasskeySignupCompleteRequest(Guid ChallengeId, AuthenticatorAttestationRawResponse Response);

public sealed record PasskeyDto(
    Guid Id,
    string AuthenticatorName,
    bool BackedUp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);
