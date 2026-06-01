using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public interface ISpacesKeyProbe
{
    Task WaitForActiveAsync(string region, string accessKeyId, string secretKey, CancellationToken ct);
}

public sealed class AwsS3SpacesKeyProbe : ISpacesKeyProbe
{
    private static readonly int[] DelaysSeconds = [2, 3, 5, 8, 13, 21, 30, 30, 30, 30, 30, 30, 30];

    public async Task WaitForActiveAsync(
        string region, string accessKeyId, string secretKey, CancellationToken ct)
    {
        var endpoint = string.Create(CultureInfo.InvariantCulture,
            $"https://{region}.digitaloceanspaces.com");
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(accessKeyId, secretKey),
            new AmazonS3Config
            {
                ServiceURL           = endpoint,
                AuthenticationRegion = region,
                ForcePathStyle       = false,
            });

        foreach (var delay in DelaysSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            try
            {
                await client.ListBucketsAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (AmazonS3Exception ex) when (
                ex.ErrorCode == "InvalidAccessKeyId" || ex.StatusCode == HttpStatusCode.Forbidden)
            {
            }
        }
        throw new TimeoutException(string.Format(
            CultureInfo.InvariantCulture,
            "DO Spaces key {0} did not activate within {1}s",
            accessKeyId, DelaysSeconds.Sum()));
    }
}
