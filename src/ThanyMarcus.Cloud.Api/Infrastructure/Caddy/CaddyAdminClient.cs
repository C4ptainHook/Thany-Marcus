namespace ThanyMarcus.Cloud.Api.Infrastructure.Caddy;

public sealed class CaddyAdminClient(HttpClient http)
{
    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct) =>
        http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
}
