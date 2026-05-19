using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed partial class SafeLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient httpClient;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> options;
    private readonly ILogger<SafeLlmClient> log;

    public SafeLlmClient(
        HttpClient httpClient,
        IOptionsMonitor<LlmIntelligenceOptions> options,
        ILogger<SafeLlmClient> log)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.log = log;
    }

    public string Mode => LlmModes.Safe;
    public string ModelName => "minicpm-v";
    public string ModelVersion => options.CurrentValue.OllamaTag;

    public async Task<T> CompleteAsync<T>(PromptId promptId, object inputContext, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(promptId);
        ArgumentNullException.ThrowIfNull(inputContext);
        var opts = options.CurrentValue;
        var promptText = ExtractPromptText(promptId, inputContext);
        var maxAttempts = Math.Max(1, opts.Retry.MaxAttempts);
        var suffix = "";
        Exception? lastError = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var requestBody = BuildRequestBody<T>(opts.OllamaTag, promptText + suffix);
            try
            {
                using var resp = await httpClient.PostAsJsonAsync("/api/generate", requestBody, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Ollama returned {(int)resp.StatusCode}: {err}");
                }
                var ollamaResp = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOpts, ct);
                if (ollamaResp is null || string.IsNullOrEmpty(ollamaResp.Response))
                {
                    throw new JsonException("Ollama returned empty response field");
                }
                // FORK: special-case T==string for hub-generate-v1 which returns free-form Markdown.
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)ollamaResp.Response.Trim();
                }
                var parsed = JsonSerializer.Deserialize<T>(ollamaResp.Response, JsonOpts)
                             ?? throw new JsonException("null deserialization");
                return parsed;
            }
            catch (JsonException ex)
            {
                lastError = ex;
                LogJsonParseFailed(log, attempt, promptId.ToString(), ex);
                suffix = "\n\nYour previous response was not valid JSON. Respond with ONLY valid JSON matching the schema.";
            }
            catch (HttpRequestException ex)
            {
                throw new LlmStructuredOutputException(promptId, attempt + 1, ex);
            }
        }
        throw new LlmStructuredOutputException(promptId, maxAttempts, lastError);
    }

    private static object BuildRequestBody<T>(string model, string prompt)
    {
        if (typeof(T) == typeof(string))
        {
            return new
            {
                model,
                prompt,
                stream = false,
                options = new { temperature = 0, num_ctx = 8192 },
            };
        }
        return new
        {
            model,
            prompt,
            stream = false,
            format = "json",
            options = new { temperature = 0, num_ctx = 8192 },
        };
    }

    private static string ExtractPromptText(PromptId promptId, object inputContext)
    {
        // Handlers build the prompt via PromptBuilder.Build*(...) and pass the resulting
        // string here. We also accept a wrapper record so the call site has a typed shape.
        if (inputContext is string s) return s;
        if (inputContext is LlmPromptRequest req) return req.PromptText;
        throw new InvalidOperationException(
            $"SafeLlmClient.CompleteAsync expects inputContext as string or LlmPromptRequest (promptId={promptId})");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "LLM JSON parse failed on attempt {Attempt} for {PromptId}")]
    private static partial void LogJsonParseFailed(ILogger logger, int attempt, string promptId, Exception ex);
}

public sealed record LlmPromptRequest(string PromptText);
