namespace ThanyMarcus.Cloud.Api.Infrastructure.Storage;

public sealed class StorageOptions
{
    public string Provider { get; init; } = "s3";
    public string Endpoint { get; init; } = "";
    public string Region { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string AccessKeyId { get; init; } = "";
    public string AccessKeySecret { get; init; } = "";
}

public static class StorageProviders
{
    public const string S3 = "s3";
    public const string AzureBlob = "azure_blob";
}
