namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloud;

public interface IPortalToCloudPluginTokenClient
{
    Task<Guid> PostAsync(
        string cloudUrl,
        string cloudAdminToken,
        byte[] tokenHashBytes,
        string label,
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
