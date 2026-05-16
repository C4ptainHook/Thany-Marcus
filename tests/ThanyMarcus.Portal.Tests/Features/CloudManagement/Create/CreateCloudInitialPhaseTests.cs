using System.Runtime.CompilerServices;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.Create;

public sealed class CreateCloudInitialPhaseTests
{
    [Fact]
    public void Initial_status_is_a_phase_handled_by_some_ISagaPhaseHandler()
    {
        const string InitialStatus = SagaStatus.TfPlanning;

        var handlerTypes = typeof(ISagaPhaseHandler).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISagaPhaseHandler).IsAssignableFrom(t))
            .ToList();

        var phases = handlerTypes
            .Select(t =>
            {
                var instance = (ISagaPhaseHandler)RuntimeHelpers.GetUninitializedObject(t);
                return instance.Phase;
            })
            .ToList();

        phases.ShouldContain(InitialStatus,
            $"CreateCloudEndpoints inserts ProvisioningJob.Status = {InitialStatus}, " +
            $"but no ISagaPhaseHandler claims that phase. Registered phases: [{string.Join(", ", phases)}]");
    }
}
