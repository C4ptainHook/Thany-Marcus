using System.Collections.Concurrent;
using NodaTime;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class FakeArtifactStore : IArtifactStore
{
    public ConcurrentDictionary<string, FakeObject> Objects { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, bool> Finalized { get; } = new(StringComparer.Ordinal);

    public sealed record FakeObject(long ByteSize, string ETag, string? MimeType);

    public Task<PresignedUpload> IssueUploadUrlAsync(
        string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult(new PresignedUpload(
            new Uri($"https://fake.example.test/upload/{Uri.EscapeDataString(key)}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = mimeType },
            SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl))));

    public bool FailDownloadUrl { get; set; }

    public Task<PresignedDownload> IssueDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        if (FailDownloadUrl)
        {
            throw new InvalidOperationException("simulated presign failure");
        }
        return Task.FromResult(new PresignedDownload(
            new Uri($"https://fake.example.test/download/{Uri.EscapeDataString(key)}"),
            SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl))));
    }

    public Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct) =>
        Task.FromResult<ObjectMetadata?>(Objects.TryGetValue(key, out var o)
            ? new ObjectMetadata(o.ByteSize, o.ETag, o.MimeType)
            : null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Objects.TryRemove(key, out _);
        Bodies.TryRemove(key, out _);
        Finalized.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        if (!Bodies.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"FakeArtifactStore has no body for key {key}");
        }
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task UploadBytesAsync(
        string key, byte[] bytes, string mimeType, bool finalized, CancellationToken ct)
    {
        Bodies[key] = bytes;
        Finalized[key] = finalized;
        Objects[key] = new FakeObject(bytes.LongLength, "fake-etag", mimeType);
        return Task.CompletedTask;
    }

    public void Seed(string key, long byteSize, string etag = "fake-etag", string? mimeType = null) =>
        Objects[key] = new FakeObject(byteSize, etag, mimeType);

    public void SeedBody(string key, byte[] body, string? mimeType = null)
    {
        Bodies[key] = body;
        Objects[key] = new FakeObject(body.LongLength, "fake-etag", mimeType);
    }
}
