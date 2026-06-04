using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public sealed class GoogleGeminiClient : ISynthesisLlmClient
{
    public const string HttpClientName = "google-gemini";

    private readonly IHttpClientFactory clientFactory;

    public GoogleGeminiClient(IHttpClientFactory clientFactory)
    {
        this.clientFactory = clientFactory;
    }

    public string ProviderId => "google";

    public async Task<SynthesisResponse> CompleteAsync(SynthesisRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
        {
            throw new SynthesisLlmException("Google API key is required in public mode");
        }

        using var http = clientFactory.CreateClient(HttpClientName);
        var url = $"/v1beta/models/{req.Model}:generateContent?key={Uri.EscapeDataString(req.ApiKey)}";

        var constrained = req.ResponseSchema is not null;
        var payload = new GeminiGenerateRequest(
            Contents: new[]
            {
                new GeminiContent("user", new[] { new GeminiPart(req.Prompt) }),
            },
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: req.Temperature,
                MaxOutputTokens: req.MaxOutputTokens,
                Seed: req.Seed,
                ResponseMimeType: constrained ? "application/json" : null,
                ResponseSchema: constrained ? GeminiSchemaProjector.Project(req.ResponseSchema!) : null));

        using var resp = await http.PostAsJsonAsync(url, payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new SynthesisLlmException($"Gemini returned {(int)resp.StatusCode}: {Truncate(err, 500)}");
        }

        var body = await resp.Content.ReadFromJsonAsync<GeminiGenerateResponse>(ct);
        var candidate = body?.Candidates is { Count: > 0 } cs ? cs[0] : null;
        var part = candidate?.Content?.Parts is { Count: > 0 } ps ? ps[0] : null;
        var text = part?.Text;
        if (string.IsNullOrEmpty(text))
        {
            throw new SynthesisLlmException("Gemini returned empty content");
        }

        return new SynthesisResponse(
            Body: text.Trim(),
            ModelTag: req.Model,
            PromptTokens: body?.UsageMetadata?.PromptTokenCount ?? 0,
            CompletionTokens: body?.UsageMetadata?.CandidatesTokenCount ?? 0);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private sealed record GeminiGenerateRequest(
        [property: JsonPropertyName("contents")]         IReadOnlyList<GeminiContent> Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("role")]  string Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")]      double Temperature,
        [property: JsonPropertyName("maxOutputTokens")]  int MaxOutputTokens,
        [property: JsonPropertyName("seed")]             int Seed,
        [property: JsonPropertyName("responseMimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ResponseMimeType = null,
        [property: JsonPropertyName("responseSchema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonNode? ResponseSchema = null);

    private sealed record GeminiGenerateResponse(
        [property: JsonPropertyName("candidates")]    IReadOnlyList<GeminiCandidate>? Candidates,
        [property: JsonPropertyName("usageMetadata")] GeminiUsageMetadata? UsageMetadata);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);

    private sealed record GeminiUsageMetadata(
        [property: JsonPropertyName("promptTokenCount")]     int PromptTokenCount,
        [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount);
}
