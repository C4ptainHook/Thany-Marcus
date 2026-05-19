namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class OllamaJsonParseException : Exception
{
    public string RawResponse { get; }

    public OllamaJsonParseException(string rawResponse, string? message = null, Exception? inner = null)
        : base(message ?? $"Failed to parse Ollama VLM response as JSON: {Truncate(rawResponse)}", inner)
    {
        RawResponse = rawResponse;
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "<empty>" : (s.Length <= 200 ? s : s[..200] + "…");
}
