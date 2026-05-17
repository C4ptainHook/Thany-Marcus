namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public sealed record TotpEnableVerifyRequest(string Secret, string Code, string? CurrentCode = null);
