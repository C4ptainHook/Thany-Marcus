using NodaTime;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Storage;

public interface IArtifactStore
{
    Task<PresignedUpload> IssueUploadUrlAsync(
        string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct);

    Task<PresignedDownload> IssueDownloadUrlAsync(
        string key, TimeSpan ttl, CancellationToken ct);

    Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task UploadBytesAsync(
        string key, byte[] bytes, string mimeType, bool finalized, CancellationToken ct);
}

public sealed record PresignedUpload(
    Uri Url,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    Instant ExpiresAt);

public sealed record PresignedDownload(Uri Url, Instant ExpiresAt);

public sealed record ObjectMetadata(long ByteSize, string ETag, string? MimeType);
