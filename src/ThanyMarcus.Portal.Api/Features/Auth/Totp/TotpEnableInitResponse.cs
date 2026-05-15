namespace ThanyMarcus.Portal.Api.Features.Auth.Totp;

public sealed record TotpEnableInitResponse(string Secret, string QrPngDataUri);
