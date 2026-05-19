using System.Text;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public static class ExtractionCacheKeys
{
    public static string ForOllama(string modelTag) => Build("ollama", modelTag);

    private static string Build(string sidecar, string modelTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelTag);
        return $"sha256:{sidecar}:{Sanitize(modelTag)}";
    }

    private static string Sanitize(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var c in tag)
        {
            sb.Append(c switch
            {
                '/' or ':' or ' ' => '-',
                _ => c,
            });
        }
        return sb.ToString();
    }
}
