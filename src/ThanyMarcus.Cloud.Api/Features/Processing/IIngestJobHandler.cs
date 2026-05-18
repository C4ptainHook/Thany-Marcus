namespace ThanyMarcus.Cloud.Api.Features.Processing;

public interface IIngestJobHandler
{
    Task HandleAsync(IngestJob job, CancellationToken ct);
}
