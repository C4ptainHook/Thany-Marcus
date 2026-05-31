using NodaTime;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class SagaTimeouts
{
    public static readonly Duration AwaitingCloudCallback = Duration.FromMinutes(90);
}
