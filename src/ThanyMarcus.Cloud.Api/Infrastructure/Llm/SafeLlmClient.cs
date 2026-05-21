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

    private readonly IHttpClientFactory clientFactory;
    private readonly IConfiguration config;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> options;
    private readonly ILogger<SafeLlmClient> log;

    private readonly AsyncLocal<string?> currentModelTag = new();

    public SafeLlmClient(
        IHttpClientFactory clientFactory,
        IConfiguration config,
        IOptionsMonitor<LlmIntelligenceOptions> options,
        ILogger<SafeLlmClient> log)
    {
        this.clientFactory = clientFactory;
        this.config = config;
        this.options = options;
        this.log = log;
    }

    public string Mode => LlmModes.Safe;

    public string ModelName
    {
        get
        {
            var tag = currentModelTag.Value;
            if (string.IsNullOrEmpty(tag)) return "";
            var colon = tag.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? tag : tag[..colon];
        }
    }

    public string ModelVersion => currentModelTag.Value ?? "";

    public async Task<T> CompleteAsync<T>(PromptId promptId, object inputContext, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(promptId);
        ArgumentNullException.ThrowIfNull(inputContext);
        var opts = options.CurrentValue;
        var promptText = ExtractPromptText(promptId, inputContext);
        var modelTag = ResolveModelTag(promptId);
        currentModelTag.Value = modelTag;
        var maxAttempts = Math.Max(1, opts.Retry.MaxAttempts);
        var suffix = "";
        Exception? lastError = null;

        using var http = clientFactory.CreateClient(OllamaClientNames.Text);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var requestBody = BuildRequestBody<T>(modelTag, promptText + suffix);
            try
            {
                using var resp = await http.PostAsJsonAsync("/api/generate", requestBody, ct);
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
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)ollamaResp.Response.Trim();
                }
                try
                {
                    var parsed = JsonSerializer.Deserialize<T>(ollamaResp.Response, JsonOpts)
                                 ?? throw new JsonException("null deserialization");
                    return parsed;
                }
                catch (JsonException)
                {
                    if (RouteV1Salvage.TryRecover<T>(promptId, ollamaResp.Response, out var salvaged) && salvaged is not null)
                    {
                        LogSalvagedRoute(log, promptId.Name, promptId.Version);
                        return salvaged;
                    }
                    throw;
                }
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

    private string ResolveModelTag(PromptId promptId)
    {
        var key = promptId.Name switch
        {
            "route"        => "IngestSaga:Models:Route:OllamaTag",
            "extract"      => "IngestSaga:Models:Entity:OllamaTag",
            "dedup"        => "IngestSaga:Models:Entity:OllamaTag",
            "hub-generate" => "IngestSaga:Models:Entity:OllamaTag",
            _              => "IngestSaga:Models:Entity:OllamaTag",
        };
        var tag = config[key];
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException(
                $"{key} is not configured (required for promptId={promptId})");
        }
        return tag;
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
        if (inputContext is string s) return s;
        if (inputContext is LlmPromptRequest req) return req.PromptText;
        throw new InvalidOperationException(
            $"SafeLlmClient.CompleteAsync expects inputContext as string or LlmPromptRequest (promptId={promptId})");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "LLM JSON parse failed on attempt {Attempt} for {PromptId}")]
    private static partial void LogJsonParseFailed(ILogger logger, int attempt, string promptId, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Salvaged route-v1 decision from non-JSON model output for {PromptName}-{PromptVersion}")]
    private static partial void LogSalvagedRoute(ILogger logger, string promptName, string promptVersion);
}

public sealed record LlmPromptRequest(string PromptText);
