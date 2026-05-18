using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

internal sealed class EnrichmentSchema
{
    [JsonPropertyName("suggested_project")]
    public string? SuggestedProject { get; set; }

    [JsonPropertyName("body_anchors")]
    public List<EnrichmentAnchor> BodyAnchors { get; set; } = new();

    [JsonPropertyName("attachment_anchors")]
    public List<EnrichmentAttachmentAnchors> AttachmentAnchors { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

internal sealed class EnrichmentAnchor
{
    [JsonPropertyName("text")]   public string Text   { get; set; } = "";
    [JsonPropertyName("start")]  public int    Start  { get; set; }
    [JsonPropertyName("end")]    public int    End    { get; set; }
    [JsonPropertyName("target")] public string Target { get; set; } = "";
}

internal sealed class EnrichmentAttachmentAnchors
{
    [JsonPropertyName("attachment_id")] public string AttachmentId { get; set; } = "";
    [JsonPropertyName("anchors")]       public List<EnrichmentAnchor> Anchors { get; set; } = new();
}
