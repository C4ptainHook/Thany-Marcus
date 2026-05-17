namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public sealed class BootstrapOptions
{
    public Guid CloudId { get; init; }
    public string Hostname { get; init; } = "";
    public string EnrollmentToken { get; init; } = "";
    public string CloudAdminToken { get; init; } = "";
    public string PortalCallbackUrl { get; init; } = "";
}
