using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class ExtractionCacheKeysTests
{
    [Fact]
    public void ForOllama_emits_sanitized_key_with_sidecar_prefix()
    {
        var key = ExtractionCacheKeys.ForOllama("openbmb/minicpm-v4.6:q4_K_M");
        key.ShouldBe("sha256:ollama:openbmb-minicpm-v4.6-q4_K_M");
    }

    [Fact]
    public void ForOllama_rejects_blank_tag()
    {
        Should.Throw<ArgumentException>(() => ExtractionCacheKeys.ForOllama(""));
    }
}
