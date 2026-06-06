namespace ThanyMarcus.Shared.CloudUpdate;

public static class CloudUpdatePhases
{
    public const string Idle = "idle";
    public const string Queued = "queued";
    public const string Verifying = "verifying";
    public const string Pulling = "pulling";
    public const string Applying = "applying";
    public const string HealthCheck = "health_check";
    public const string Committed = "committed";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";
    public const string RequiresBlueGreen = "requires_blue_green";
    public const string UpToDate = "up_to_date";
}
