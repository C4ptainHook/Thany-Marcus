namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class DoclingOptions
{
    public string BaseUrl { get; init; } = "http://docling:5001";
    public string HealthPath { get; init; } = "/health";
    // Path may shift to /v1 in a future docling-serve image; override via appsettings if needed.
    public string ConvertSourcePath { get; init; } = "/v1alpha/convert/source";
    public int RequestTimeoutSeconds { get; init; } = 120;
    public TimeSpan PresignedUrlTtl { get; init; } = TimeSpan.FromMinutes(5);
}
