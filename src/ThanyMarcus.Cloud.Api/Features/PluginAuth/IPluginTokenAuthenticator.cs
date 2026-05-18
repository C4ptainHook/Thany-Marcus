namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public interface IPluginTokenAuthenticator
{
    Task<PluginPrincipal?> AuthenticateAsync(string? authorizationHeader, CancellationToken ct);
}
