using System.Text.Json.Nodes;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public static class GeminiSchemaProjector
{
    private static readonly string[] UnsupportedKeys =
    {
        "additionalProperties", "maxLength", "minLength", "$schema",
    };

    public static JsonNode Project(JsonNode logical)
    {
        ArgumentNullException.ThrowIfNull(logical);
        var clone = logical.DeepClone();
        Walk(clone);
        return clone;
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in UnsupportedKeys) obj.Remove(key);
                if (obj["properties"] is JsonObject props)
                {
                    var ordering = new JsonArray();
                    foreach (var prop in props)
                    {
                        ordering.Add(prop.Key);
                        Walk(prop.Value);
                    }
                    obj["propertyOrdering"] = ordering;
                }
                Walk(obj["items"]);
                break;
            case JsonArray arr:
                foreach (var item in arr) Walk(item);
                break;
        }
    }
}
