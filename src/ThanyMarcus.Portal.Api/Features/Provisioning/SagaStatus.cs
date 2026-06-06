namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class SagaStatus
{
    public const string Pending               = "pending";
    public const string MintingSpaces         = "minting_spaces";
    public const string TfPlanning            = "tf_planning";
    public const string TfApplying            = "tf_applying";
    public const string DnsCreating           = "dns_creating";
    public const string AwaitingCloudCallback = "awaiting_cloud_callback";
    public const string AwaitingCert          = "awaiting_cert";
    public const string IssuingPluginToken    = "issuing_plugin_token";
    public const string RollingBackTf         = "rolling_back_tf";
    public const string RollingBackDns        = "rolling_back_dns";
    public const string Destroying            = "destroying";

    public const string MigrateQuiescing      = "migrate_quiescing";
    public const string MigrateSnapshotting   = "migrate_snapshotting";
    public const string MigrateProvisioning   = "migrate_provisioning";
    public const string MigrateVerifying      = "migrate_verifying";
    public const string MigrateCutover        = "migrate_cutover";
    public const string MigratePostGate       = "migrate_post_gate";
    public const string MigrateDestroyingOld  = "migrate_destroying_old";

    public const string Succeeded            = "succeeded";
    public const string FailedTf             = "failed_tf";
    public const string FailedDns            = "failed_dns";
    public const string FailedCallback       = "failed_callback";
    public const string FailedCert           = "failed_cert";
    public const string FailedPluginToken    = "failed_plugin_token";
    public const string FailedDestroy        = "failed_destroy";
    public const string FailedMintingSpaces  = "failed_minting_spaces";
    public const string Cancelled            = "cancelled";
    public const string RolledBack           = "rolled_back";

    public const string MigrateSucceeded     = "migrate_succeeded";
    public const string FailedMigrate        = "failed_migrate";
    public const string MigrateRolledBack    = "migrate_rolled_back";

    public static readonly IReadOnlyList<string> Terminal =
    [
        Succeeded, FailedTf, FailedDns, FailedCallback, FailedCert, FailedPluginToken, FailedDestroy, FailedMintingSpaces, Cancelled, RolledBack,
        MigrateSucceeded, FailedMigrate, MigrateRolledBack,
    ];

    private static readonly HashSet<string> TerminalSet = new(Terminal, StringComparer.Ordinal);

    public static bool IsTerminal(string status) => TerminalSet.Contains(status);
}
