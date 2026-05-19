namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public interface IPhaseHandler
{
    string Phase { get; }
    Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct);
}

public enum PhaseHandlerResult
{
    Advanced,
    Waiting,
}
