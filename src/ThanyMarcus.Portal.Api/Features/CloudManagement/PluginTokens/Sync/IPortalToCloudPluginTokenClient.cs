namespace ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;

public interface IPortalToCloudPluginTokenClient
{
    Task<Guid> PostAsync(
        string cloudUrl,
        string cloudAdminToken,
        byte[] tokenHashBytes,
        string label,
        CancellationToken ct);

    Task RevokeAsync(
        string cloudUrl,
        string cloudAdminToken,
        byte[] tokenHashBytes,
        CancellationToken ct);
}

public sealed class PluginTokenSyncException : Exception
{
    public int? StatusCode { get; }

    public PluginTokenSyncException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
