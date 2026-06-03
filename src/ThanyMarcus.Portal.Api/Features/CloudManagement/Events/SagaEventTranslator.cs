using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Events;

public sealed record JobStateSnapshot(
    Guid JobId,
    Guid CloudId,
    string Status,
    string Hostname,
    string? Ip,
    string? LastError,
    bool CancelRequested);

public sealed class SagaEventTranslator(IClock clock)
{
    private static readonly HashSet<string> PhaseStatuses =
        new(StringComparer.Ordinal)
        {
            SagaStatus.TfPlanning,
            SagaStatus.TfApplying,
            SagaStatus.DnsCreating,
            SagaStatus.AwaitingCloudCallback,
            SagaStatus.AwaitingCert,
            SagaStatus.IssuingPluginToken,
        };

    public IEnumerable<WizardSseEvent> InitialEvents(JobStateSnapshot? state)
    {
        if (state is null) yield break;
        if (SagaStatus.IsTerminal(state.Status))
        {
            yield return TerminalEvent(state);
            yield break;
        }
        if (PhaseStatuses.Contains(state.Status))
        {
            yield return WizardSseEvent.PhaseStarted(state.Status, NowIso());
        }
    }

    public IEnumerable<WizardSseEvent> Translate(JobStateSnapshot? prev, JobStateSnapshot curr)
    {
        if (prev is not null && string.Equals(prev.Status, curr.Status, StringComparison.Ordinal))
            yield break;

        var at = NowIso();
        if (prev is not null && PhaseStatuses.Contains(prev.Status))
            yield return WizardSseEvent.PhaseCompleted(prev.Status, at);

        if (SagaStatus.IsTerminal(curr.Status))
        {
            yield return TerminalEvent(curr);
        }
        else if (PhaseStatuses.Contains(curr.Status))
        {
            yield return WizardSseEvent.PhaseStarted(curr.Status, at);
        }
    }

    private static WizardSseEvent TerminalEvent(JobStateSnapshot state) => state.Status switch
    {
        SagaStatus.Succeeded   => WizardSseEvent.CloudReady(state.CloudId, state.Hostname, state.Ip),
        SagaStatus.Cancelled   => WizardSseEvent.CloudCancelled(state.LastError ?? "cancelled"),
        SagaStatus.RolledBack when state.CancelRequested
                               => WizardSseEvent.CloudCancelled(state.LastError ?? "cancelled"),
        SagaStatus.RolledBack  => WizardSseEvent.CloudRolledBack(state.LastError ?? "rolled_back"),
        _                      => WizardSseEvent.CloudFailed(state.Status, state.Status, state.LastError ?? ""),
    };

    private string NowIso() => clock.GetCurrentInstant().ToString();
}
