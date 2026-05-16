namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class SagaStatus
{
    public const string Pending               = "pending";
    public const string TfPlanning            = "tf_planning";
    public const string TfApplying            = "tf_applying";
    public const string DnsCreating           = "dns_creating";
    public const string AwaitingCloudCallback = "awaiting_cloud_callback";
    public const string AwaitingCert          = "awaiting_cert";
    public const string RollingBackTf         = "rolling_back_tf";
    public const string RollingBackDns        = "rolling_back_dns";
    public const string Destroying            = "destroying";

    public const string Succeeded       = "succeeded";
    public const string FailedTf        = "failed_tf";
    public const string FailedDns       = "failed_dns";
    public const string FailedCallback  = "failed_callback";
    public const string FailedCert      = "failed_cert";
    public const string FailedDestroy   = "failed_destroy";
    public const string Cancelled       = "cancelled";
    public const string RolledBack      = "rolled_back";

    public static readonly IReadOnlyList<string> Terminal =
    [
        Succeeded, FailedTf, FailedDns, FailedCallback, FailedCert, FailedDestroy, Cancelled, RolledBack,
    ];

    private static readonly HashSet<string> TerminalSet = new(Terminal, StringComparer.Ordinal);

    public static bool IsTerminal(string status) => TerminalSet.Contains(status);
}
