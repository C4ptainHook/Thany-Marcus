namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Events;

public abstract record WizardSseEvent(string Type)
{
    public sealed record PhaseStartedEvent(string Phase, string At)
        : WizardSseEvent("phase_started");
    public sealed record PhaseCompletedEvent(string Phase, string At)
        : WizardSseEvent("phase_completed");
    public sealed record PhaseFailedEvent(string Phase, string Reason, string Message)
        : WizardSseEvent("phase_failed");
    public sealed record CloudReadyEvent(Guid CloudId, string Hostname, string? Ip)
        : WizardSseEvent("cloud_ready");
    public sealed record CloudFailedEvent(string TerminalStatus, string Reason, string Message)
        : WizardSseEvent("cloud_failed");
    public sealed record CloudRolledBackEvent(string Reason)
        : WizardSseEvent("cloud_rolled_back");
    public sealed record CloudCancelledEvent(string Reason)
        : WizardSseEvent("cloud_cancelled");
    public sealed record PluginTokenIssuedEvent(string RawToken, string DeepLink)
        : WizardSseEvent("plugin_token_issued");

    public static PhaseStartedEvent PhaseStarted(string phase, string at) => new(phase, at);
    public static PhaseCompletedEvent PhaseCompleted(string phase, string at) => new(phase, at);
    public static PhaseFailedEvent PhaseFailed(string phase, string reason, string message)
        => new(phase, reason, message);
    public static CloudReadyEvent CloudReady(Guid cloudId, string hostname, string? ip)
        => new(cloudId, hostname, ip);
    public static CloudFailedEvent CloudFailed(string terminalStatus, string reason, string message)
        => new(terminalStatus, reason, message);
    public static CloudRolledBackEvent CloudRolledBack(string reason) => new(reason);
    public static CloudCancelledEvent CloudCancelled(string reason) => new(reason);
    public static PluginTokenIssuedEvent PluginTokenIssued(string rawToken, string deepLink)
        => new(rawToken, deepLink);
}
