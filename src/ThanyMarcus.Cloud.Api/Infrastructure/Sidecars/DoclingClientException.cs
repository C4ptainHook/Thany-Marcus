namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class DoclingClientException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public DoclingClientException(string message) : base(message) { }

    public DoclingClientException(string message, Exception inner) : base(message, inner) { }

    public DoclingClientException(string message, int statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
