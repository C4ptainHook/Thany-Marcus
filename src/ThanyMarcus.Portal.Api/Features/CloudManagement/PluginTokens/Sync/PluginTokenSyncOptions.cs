namespace ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;

public sealed class PluginTokenSyncOptions
{
    public const string SectionName = "Saga:PluginTokenSync";

    public int HttpTimeoutSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 3;
}
