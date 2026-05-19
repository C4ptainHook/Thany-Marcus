namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

public sealed class GraniteEmbeddingOptions
{
    public string ModelPath { get; set; } = "/app/models/granite/model.onnx";
    public string TokenizerPath { get; set; } = "/app/models/granite/tokenizer.json";
    public int MaxTokens { get; set; } = 512;
    public int EmbeddingDim { get; set; } = 256;
    public int FullDim { get; set; } = 768;
    public string ModelTag { get; set; } = "ibm-granite/granite-embedding-311m-multilingual-r2";
    public string ModelRevision { get; set; } = "main";
    public int MaxConcurrency { get; set; } = 2;
}
