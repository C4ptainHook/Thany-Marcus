using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

public sealed class InstantSpacesKeyProbe : ISpacesKeyProbe
{
    public Task WaitForActiveAsync(string region, string accessKeyId, string secretKey, CancellationToken ct) =>
        Task.CompletedTask;
}
