using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public sealed class OllamaSynthesisLlmClient : ISynthesisLlmClient
{
    private const string LocalModelTag = "qwen3:1.7b-q4_K_M";

    private readonly IHttpClientFactory clientFactory;
    private readonly IConfiguration config;

    public OllamaSynthesisLlmClient(IHttpClientFactory clientFactory, IConfiguration config)
    {
        this.clientFactory = clientFactory;
        this.config = config;
    }

    public string ProviderId => "ollama";

    public async Task<SynthesisResponse> CompleteAsync(SynthesisRequest req, CancellationToken ct)
    {
        var modelTag = config["IngestSaga:Models:Synthesis:OllamaTag"] ?? LocalModelTag;
        using var http = clientFactory.CreateClient(OllamaClientNames.Text);

        var payload = new OllamaGenerateRequest(
            Model: modelTag,
            Prompt: req.Prompt,
            Stream: false,
            Think: false,
            Options: new OllamaOptions(
                Temperature: req.Temperature,
                Seed: req.Seed,
                NumCtx: 8192,
                NumPredict: req.MaxOutputTokens,
                RepeatPenalty: 1.25,
                RepeatLastN: 256),
            Format: req.ResponseSchema);

        using var resp = await http.PostAsJsonAsync("/api/generate", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new SynthesisLlmException($"Ollama returned {(int)resp.StatusCode}: {err}");
        }

        var body = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
        if (body is null || string.IsNullOrEmpty(body.Response))
        {
            throw new SynthesisLlmException("Ollama returned empty response");
        }

        return new SynthesisResponse(
            Body: ThinkingStripper.Strip(body.Response).Trim(),
            ModelTag: modelTag,
            PromptTokens: 0,
            CompletionTokens: 0);
    }

    private sealed record OllamaGenerateRequest(
        [property: JsonPropertyName("model")]   string Model,
        [property: JsonPropertyName("prompt")]  string Prompt,
        [property: JsonPropertyName("stream")]  bool Stream,
        [property: JsonPropertyName("think")]   bool Think,
        [property: JsonPropertyName("options")] OllamaOptions Options,
        [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonNode? Format);

    private sealed record OllamaOptions(
        [property: JsonPropertyName("temperature")]    double Temperature,
        [property: JsonPropertyName("seed")]           int Seed,
        [property: JsonPropertyName("num_ctx")]        int NumCtx,
        [property: JsonPropertyName("num_predict")]    int NumPredict,
        [property: JsonPropertyName("repeat_penalty")] double RepeatPenalty,
        [property: JsonPropertyName("repeat_last_n")]  int RepeatLastN);
}
