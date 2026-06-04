using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public sealed class SynthesisLlmRouter : ISynthesisLlmRouter
{
    private const string LocalModelTag = "qwen3:1.7b-q4_K_M";

    private readonly OllamaSynthesisLlmClient ollama;
    private readonly GoogleGeminiClient google;
    private readonly IConfiguration config;

    public SynthesisLlmRouter(
        OllamaSynthesisLlmClient ollama,
        GoogleGeminiClient google,
        IConfiguration config)
    {
        this.ollama = ollama;
        this.google = google;
        this.config = config;
    }

    public ISynthesisLlmClient Resolve(string privacyMode, string? publicModel)
    {
        if (privacyMode == PrivacyModes.Private) return ollama;
        if (publicModel is null) throw new InvalidOperationException("publicModel required in public mode");
        return PublicSynthesisModels.ProviderOf(publicModel) switch
        {
            "google" => google,
            var p    => throw new InvalidOperationException($"unsupported provider '{p}'"),
        };
    }

    public string ResolveModelTag(string privacyMode, string? publicModel) =>
        privacyMode == PrivacyModes.Private
            ? config["IngestSaga:Models:Synthesis:OllamaTag"] ?? LocalModelTag
            : publicModel ?? throw new InvalidOperationException("publicModel required");
}
