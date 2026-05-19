namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class UrlFetcherException : Exception
{
    public string Reason { get; }

    public UrlFetcherException(string reason, string? message = null, Exception? inner = null)
        : base(message ?? reason, inner)
    {
        Reason = reason;
    }
}
