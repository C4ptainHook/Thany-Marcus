using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class EventsLogAppender
{
    public static void Append(ProvisioningJob job, IClock clock, string phase, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var arr = ToArray(job.EventsLog);
        body["phase"] = phase;
        body["timestamp"] = clock.GetCurrentInstant().ToString();
        arr.Add(body);
        ReplaceEventsLog(job, arr);
    }

    public static void AppendTerraformStream(ProvisioningJob job, IClock clock, string phase, string source, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return;

        var arr = ToArray(job.EventsLog);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(line); }
            catch (JsonException) { parsed = null; }

            JsonObject entry;
            if (parsed is JsonObject obj)
            {
                entry = obj;
                entry["source"] = source;
            }
            else
            {
                entry = new JsonObject
                {
                    ["source"] = source,
                    ["raw"] = line,
                };
            }
            entry["phase"] = phase;
            entry["timestamp"] = clock.GetCurrentInstant().ToString();
            arr.Add(entry);
        }
        ReplaceEventsLog(job, arr);
    }

    private static JsonArray ToArray(JsonDocument doc)
    {
        var node = JsonNode.Parse(doc.RootElement.GetRawText());
        return node as JsonArray ?? [];
    }

    private static void ReplaceEventsLog(ProvisioningJob job, JsonArray arr)
    {
        var previous = job.EventsLog;
        job.EventsLog = JsonDocument.Parse(arr.ToJsonString());
        previous.Dispose();
    }
}
