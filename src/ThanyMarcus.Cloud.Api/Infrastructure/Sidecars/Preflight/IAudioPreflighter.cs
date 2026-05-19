using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public interface IAudioPreflighter
{
    Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct);
}
