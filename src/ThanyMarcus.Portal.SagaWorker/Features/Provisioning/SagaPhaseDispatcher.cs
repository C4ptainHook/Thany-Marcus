using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public sealed partial class SagaPhaseDispatcher
{
    private readonly Dictionary<string, ISagaPhaseHandler> handlers;
    private readonly ILogger<SagaPhaseDispatcher> log;

    public SagaPhaseDispatcher(IEnumerable<ISagaPhaseHandler> handlers, ILogger<SagaPhaseDispatcher> log)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = new Dictionary<string, ISagaPhaseHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (!this.handlers.TryAdd(h.Phase, h))
            {
                throw new InvalidOperationException($"Duplicate ISagaPhaseHandler registration for phase '{h.Phase}'");
            }
        }
        this.log = log;
    }

    public IReadOnlyCollection<string> KnownPhases => handlers.Keys;

    public Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        if (SagaStatus.IsTerminal(job.Status))
        {
            LogTerminalClaim(log, job.Id, job.Status);
            return Task.CompletedTask;
        }
        if (!handlers.TryGetValue(job.Status, out var handler))
        {
            LogNoHandler(log, job.Id, job.Status);
            return Task.CompletedTask;
        }
        return handler.HandleAsync(job, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Claim observed terminal status; dropping (job={JobId} status={Status})")]
    private static partial void LogTerminalClaim(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "No handler registered for status (job={JobId} status={Status})")]
    private static partial void LogNoHandler(ILogger logger, Guid jobId, string status);
}
