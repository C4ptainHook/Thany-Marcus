using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;
using TokenizersDotnet = Tokenizers.DotNet;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Embedding;

public sealed partial class GraniteEmbeddingClient : IEmbeddingClient, IDisposable
{
    private readonly GraniteEmbeddingOptions opts;
    private readonly InferenceSession session;
    private readonly TokenizersDotnet.Tokenizer tokenizer;
    private readonly SemaphoreSlim sem;
    private readonly string[] inputNames;
    private readonly bool needsTokenTypeIds;
    private readonly ILogger<GraniteEmbeddingClient> log;

    public GraniteEmbeddingClient(IOptions<GraniteEmbeddingOptions> opts, ILogger<GraniteEmbeddingClient> log)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(log);
        this.opts = opts.Value;
        this.log = log;
        this.sem = new SemaphoreSlim(this.opts.MaxConcurrency, this.opts.MaxConcurrency);

        var sessionOpts = new OrtSessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            IntraOpNumThreads = 0,
        };
        this.session = new InferenceSession(this.opts.ModelPath, sessionOpts);
        this.inputNames = this.session.InputMetadata.Keys.ToArray();
        this.needsTokenTypeIds = this.inputNames.Any(n =>
            string.Equals(n, "token_type_ids", StringComparison.Ordinal));

        this.tokenizer = new TokenizersDotnet.Tokenizer(vocabPath: this.opts.TokenizerPath);

        var sw = Stopwatch.StartNew();
        _ = this.tokenizer.Encode("warmup");
        LogInitialized(log, this.opts.ModelTag, this.opts.EmbeddingDim, this.inputNames.Length, sw.ElapsedMilliseconds);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "GraniteEmbeddingClient initialized: model={Tag} dim={Dim} inputCount={InputCount} init={Ms}ms")]
    private static partial void LogInitialized(ILogger logger, string tag, int dim, int inputCount, long ms);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Embed(text ?? "");
        }
        finally
        {
            sem.Release();
        }
    }

    private float[] Embed(string text)
    {
        var ids = TokenizeAndTruncate(text);
        var inputs = BuildInputs(ids);

        using var results = session.Run(inputs);
        var firstOutput = results[0];
        var lhs = firstOutput.AsTensor<float>();
        var hidden = lhs.Dimensions[2];
        var pooled = ClsPool(lhs, hidden);
        L2NormalizeInPlace(pooled);

        var dim = opts.EmbeddingDim;
        var cut = pooled.Length == dim ? pooled : pooled.AsSpan(0, dim).ToArray();
        L2NormalizeInPlace(cut);
        return cut;
    }

    private long[] TokenizeAndTruncate(string text)
    {
        var encoded = tokenizer.Encode(text);
        var max = opts.MaxTokens;
        var len = Math.Min(encoded.Length, max);
        if (len == 0)
        {
            return new long[] { 0L };
        }
        var ids = new long[len];
        for (var i = 0; i < len; i++)
        {
            ids[i] = encoded[i];
        }
        return ids;
    }

    private List<NamedOnnxValue> BuildInputs(long[] ids)
    {
        var seqLen = ids.Length;
        var inputIds = new DenseTensor<long>(ids, new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new long[seqLen], new[] { 1, seqLen });
        for (var i = 0; i < seqLen; i++) attentionMask[0, i] = 1L;

        var list = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };
        if (needsTokenTypeIds)
        {
            var tokenTypeIds = new DenseTensor<long>(new long[seqLen], new[] { 1, seqLen });
            list.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }
        return list;
    }

    private static float[] ClsPool(Tensor<float> lhs, int hidden)
    {
        var pooled = new float[hidden];
        for (var h = 0; h < hidden; h++) pooled[h] = lhs[0, 0, h];
        return pooled;
    }

    private static void L2NormalizeInPlace(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;
        var inv = (float)(1.0 / norm);
        for (var i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose()
    {
        sem.Dispose();
        session.Dispose();
    }
}
