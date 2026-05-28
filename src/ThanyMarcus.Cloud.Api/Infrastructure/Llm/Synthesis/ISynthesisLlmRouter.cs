using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;

public interface ISynthesisLlmRouter
{
    /// <summary>
    /// Resolves the appropriate synthesis backend for the requested mode and model.
    /// In private mode returns the local Ollama-backed client and ignores model/apiKey.
    /// In public mode returns the provider client for the requested model.
    /// </summary>
    ISynthesisLlmClient Resolve(string privacyMode, string? publicModel);

    /// <summary>
    /// Returns the canonical model tag that will be sent to the provider for the given
    /// (privacyMode, publicModel) pair — used for cache keys and frontmatter.
    /// </summary>
    string ResolveModelTag(string privacyMode, string? publicModel);
}
