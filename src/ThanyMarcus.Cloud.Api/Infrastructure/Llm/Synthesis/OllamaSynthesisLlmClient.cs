using System.Net.Http.Json;
using System.Text.Json;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public sealed class OllamaSynthesisLlmClient : ISynthesisLlmClient
{
    private const string LocalModelTag = "qwen3-1.7b-q4_k_m";

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

        var payload = new
        {
            model = modelTag,
            prompt = req.Prompt,
            stream = false,
            options = new
            {
                temperature  = req.Temperature,
                seed         = req.Seed,
                num_ctx      = 8192,
                num_predict  = req.MaxOutputTokens,
            },
        };

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
            Body: body.Response.Trim(),
            ModelTag: modelTag,
            PromptTokens: 0,
            CompletionTokens: 0);
    }
}
