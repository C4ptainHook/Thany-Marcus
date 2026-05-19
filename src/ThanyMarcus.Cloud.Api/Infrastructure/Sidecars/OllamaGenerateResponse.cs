using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed record OllamaGenerateResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("done_reason")] string? DoneReason,
    [property: JsonPropertyName("eval_count")] long EvalCount,
    [property: JsonPropertyName("eval_duration")] long EvalDuration);
