using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

public static class UrlExtractionExtra
{
    public static JsonDocument Build(UrlExtractionResult r)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("canonical_url", r.CanonicalUrl);
            writer.WriteString("title", r.Title);
            writer.WriteString("description", r.Description);
            writer.WriteString("author_name", r.AuthorName);
            writer.WriteString("provider_name", r.ProviderName);
            writer.WriteString("thumbnail_url", r.ThumbnailUrl);
            writer.WriteNumber("http_status", r.HttpStatus);
            writer.WriteString("minimal_reason", r.MinimalReason);
            writer.WriteNull("redirected_to");
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }
}
