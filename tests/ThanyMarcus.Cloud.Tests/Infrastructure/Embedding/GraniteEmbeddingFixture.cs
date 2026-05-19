using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

public sealed class GraniteEmbeddingFixture : IDisposable
{
    public GraniteEmbeddingClient? Client { get; }
    public bool ModelAvailable { get; }
    public string? SkipReason { get; }

    public GraniteEmbeddingFixture()
    {
        var (modelPath, tokenizerPath) = ResolvePaths();
        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            ModelAvailable = false;
            SkipReason = $"Granite model not staged at {modelPath} / {tokenizerPath}. " +
                         "Run scripts/download-granite-model.sh to enable slow-lane embedding tests.";
            return;
        }

        var opts = new GraniteEmbeddingOptions
        {
            ModelPath = modelPath,
            TokenizerPath = tokenizerPath,
        };
        Client = new GraniteEmbeddingClient(
            Options.Create(opts),
            NullLogger<GraniteEmbeddingClient>.Instance);
        ModelAvailable = true;
    }

    private static (string modelPath, string tokenizerPath) ResolvePaths()
    {
        var envModel = Environment.GetEnvironmentVariable("GRANITE_MODEL_PATH");
        var envTok = Environment.GetEnvironmentVariable("GRANITE_TOKENIZER_PATH");
        if (!string.IsNullOrEmpty(envModel) && !string.IsNullOrEmpty(envTok))
        {
            return (envModel, envTok);
        }
        var repoRoot = FindRepoRoot();
        var baseDir = Path.Combine(repoRoot, "src", "ThanyMarcus.Cloud.Api", "models", "granite");
        return (
            Path.Combine(baseDir, "onnx", "model.onnx"),
            Path.Combine(baseDir, "tokenizer.json"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        Client?.Dispose();
    }
}
