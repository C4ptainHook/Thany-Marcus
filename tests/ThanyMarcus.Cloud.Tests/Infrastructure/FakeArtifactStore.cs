using System.Collections.Concurrent;
using NodaTime;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class FakeArtifactStore : IArtifactStore
{
    public ConcurrentDictionary<string, FakeObject> Objects { get; } = new(StringComparer.Ordinal);

    public sealed record FakeObject(long ByteSize, string ETag, string? MimeType);

    public Task<PresignedUpload> IssueUploadUrlAsync(
        string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult(new PresignedUpload(
            new Uri($"https://fake.example.test/upload/{Uri.EscapeDataString(key)}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = mimeType },
            SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl))));

    public Task<PresignedDownload> IssueDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult(new PresignedDownload(
            new Uri($"https://fake.example.test/download/{Uri.EscapeDataString(key)}"),
            SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl))));

    public Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct) =>
        Task.FromResult<ObjectMetadata?>(Objects.TryGetValue(key, out var o)
            ? new ObjectMetadata(o.ByteSize, o.ETag, o.MimeType)
            : null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public void Seed(string key, long byteSize, string etag = "fake-etag", string? mimeType = null) =>
        Objects[key] = new FakeObject(byteSize, etag, mimeType);
}
