namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed class DigitalOceanOAuthOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;

    public string AuthorizeEndpoint { get; set; } = "https://cloud.digitalocean.com/v1/oauth/authorize";
    public string TokenEndpoint     { get; set; } = "https://cloud.digitalocean.com/v1/oauth/token";
    public string RevokeEndpoint    { get; set; } = "https://cloud.digitalocean.com/v1/oauth/revoke";
    public string ApiBaseUrl        { get; set; } = "https://api.digitalocean.com/";
}
