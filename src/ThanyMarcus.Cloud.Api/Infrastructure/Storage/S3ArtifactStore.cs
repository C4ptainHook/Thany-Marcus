using Amazon.S3;
using Amazon.S3.Model;
using NodaTime;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Storage;

public sealed class S3ArtifactStore : IArtifactStore
{
    private readonly IAmazonS3 client;
    private readonly StorageOptions options;
    private readonly IClock clock;

    public S3ArtifactStore(IAmazonS3 client, StorageOptions options, IClock clock)
    {
        this.client = client;
        this.options = options;
        this.clock = clock;
    }

    public async Task<PresignedUpload> IssueUploadUrlAsync(
        string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName  = options.Bucket,
            Key         = key,
            Verb        = HttpVerb.PUT,
            Expires     = DateTime.UtcNow.Add(ttl),
            ContentType = mimeType,
        };

        var url = await client.GetPreSignedURLAsync(req).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"]   = mimeType,
            ["Content-Length"] = byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return new PresignedUpload(
            new Uri(url),
            headers,
            clock.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl)));
    }

    public async Task<PresignedDownload> IssueDownloadUrlAsync(
        string key, TimeSpan ttl, CancellationToken ct)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key        = key,
            Verb       = HttpVerb.GET,
            Expires    = DateTime.UtcNow.Add(ttl),
        };
        var url = await client.GetPreSignedURLAsync(req).ConfigureAwait(false);
        return new PresignedDownload(
            new Uri(url),
            clock.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl)));
    }

    public async Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = options.Bucket,
                Key        = key,
            }, ct).ConfigureAwait(false);

            return new ObjectMetadata(
                ByteSize: resp.ContentLength,
                ETag:     TrimEtag(resp.ETag),
                MimeType: resp.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = options.Bucket,
            Key        = key,
        }, ct).ConfigureAwait(false);

    private static string TrimEtag(string? etag) =>
        (etag ?? "").Trim('"');
}
