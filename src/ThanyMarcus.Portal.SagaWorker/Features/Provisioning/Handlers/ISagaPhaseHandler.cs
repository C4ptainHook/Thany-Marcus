using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public interface ISagaPhaseHandler
{
    string Phase { get; }

    Task HandleAsync(ProvisioningJob job, CancellationToken ct);
}
