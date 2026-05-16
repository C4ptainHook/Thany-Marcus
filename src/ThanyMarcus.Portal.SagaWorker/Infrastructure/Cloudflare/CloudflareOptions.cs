namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

public sealed class CloudflareOptions
{
    public string ZoneId { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public bool UseStub { get; set; }
}
