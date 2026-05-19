namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class ParakeetClientException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public ParakeetClientException(string message) : base(message) { }

    public ParakeetClientException(string message, Exception inner) : base(message, inner) { }

    public ParakeetClientException(string message, int statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
