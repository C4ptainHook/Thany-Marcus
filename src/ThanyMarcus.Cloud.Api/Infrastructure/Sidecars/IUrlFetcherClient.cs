using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IUrlFetcherClient
{
    Task<UrlFetchOutcome> FetchAsync(Guid noteId, Attachment att, CancellationToken ct);
}

public sealed record UrlFetchOutcome(
    string? ExtractedText,
    JsonDocument Extra,
    Guid? RedirectedToAttachmentId,
    bool IsMinimal = false);
