namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public sealed record TotpEnableVerifyResponse(IReadOnlyList<string> BackupCodes);
