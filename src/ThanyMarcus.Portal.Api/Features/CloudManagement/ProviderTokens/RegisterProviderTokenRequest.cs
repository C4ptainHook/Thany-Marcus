namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public sealed record RegisterProviderTokenRequest(string Provider, string Token, bool Replace = false);
