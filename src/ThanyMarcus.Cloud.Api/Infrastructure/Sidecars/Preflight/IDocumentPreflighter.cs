using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public interface IDocumentPreflighter
{
    Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct);
}
