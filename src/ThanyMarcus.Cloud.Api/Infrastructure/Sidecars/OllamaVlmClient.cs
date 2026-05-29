using System.Globalization;
using System.Text.Json;
using CoenM.ImageHash.HashAlgorithms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class OllamaVlmClient : IVlmClient
{
    private static readonly JsonSerializerOptions GenerateResponseJson = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly IHttpClientFactory clientFactory;
    private readonly IArtifactStore store;
    private readonly IFfmpegRunner ffmpeg;
    private readonly IConfiguration config;
    private readonly ILogger<OllamaVlmClient> log;

    public OllamaVlmClient(
        IHttpClientFactory clientFactory,
        IArtifactStore store,
        IFfmpegRunner ffmpeg,
        IConfiguration config,
        ILogger<OllamaVlmClient> log)
    {
        this.clientFactory = clientFactory;
        this.store = store;
        this.ffmpeg = ffmpeg;
        this.config = config;
        this.log = log;
    }

    public async Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct)
    {
        var minDim = config.GetValue("IngestSaga:Filters:Image:MinDimension", 100);
        var maxDim = config.GetValue("IngestSaga:Filters:Image:MaxDimension", 16384);
        var enableBlur = config.GetValue("IngestSaga:Filters:Image:EnableBlurCheck", false);
        var blurThreshold = config.GetValue("IngestSaga:Filters:Image:LaplacianVarianceThreshold", 50.0);
        var modelTag = config["IngestSaga:Models:Vlm:OllamaTag"] ?? "qwen3-vl:4b";
        var numCtx = config.GetValue("IngestSaga:Models:Vlm:NumCtx", 4096);
        var temperature = config.GetValue("IngestSaga:Models:Vlm:Temperature", 0.2);
        var presignTtl = TimeSpan.FromMinutes(5);

        byte[] imageBytes;

        if (string.Equals(att.StorageProvider, "external", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(att.Url))
            {
                throw new InvalidOperationException($"external attachment {att.Id} has no url");
            }
            imageBytes = await DownloadAsync(new Uri(att.Url), ct);
        }
        else
        {
            var presigned = await store.IssueDownloadUrlAsync(att.StorageKey, presignTtl, ct);
            imageBytes = await DownloadAsync(presigned.Url, ct);
        }

        var headerInfo = Image.Identify(imageBytes);
        var srcWidth = headerInfo.Width;
        var srcHeight = headerInfo.Height;

        if (srcWidth < minDim || srcHeight < minDim ||
            srcWidth > maxDim || srcHeight > maxDim)
        {
            var reason = $"dimensions_out_of_range:{srcWidth}x{srcHeight}";
            return Skipped(reason, srcWidth, srcHeight, exif: null, phash: null, blurScore: null);
        }

        using var image = Image.Load<Rgba32>(imageBytes);

        var exif = ReadExif(image.Metadata.ExifProfile);

        var phasher = new PerceptualHash();
        var phash = phasher.Hash(image);
        var phashHex = phash.ToString("x16", CultureInfo.InvariantCulture);

        double? blurScore = null;
        if (enableBlur)
        {
            blurScore = ComputeLaplacianVariance(image);
            if (blurScore.Value < blurThreshold)
            {
                var reason = $"too_blurry:variance={blurScore.Value.ToString("F1", CultureInfo.InvariantCulture)}";
                return Skipped(reason, srcWidth, srcHeight, exif, phashHex, blurScore);
            }
        }

        var vlmMaxEdge = config.GetValue("IngestSaga:Filters:Image:VlmMaxDimension", 1024);
        var vlmJpegQscale = config.GetValue("IngestSaga:Filters:Image:VlmJpegQscale", 5);
        var vlmResizeTimeoutSeconds = config.GetValue("IngestSaga:Filters:Image:VlmResizeTimeoutSeconds", 30);
        var srcLongEdge = Math.Max(srcWidth, srcHeight);
        if (srcLongEdge > vlmMaxEdge)
        {
            imageBytes = await ResizeViaFfmpegAsync(
                imageBytes, vlmMaxEdge, vlmJpegQscale,
                TimeSpan.FromSeconds(vlmResizeTimeoutSeconds), ct);
        }

        var prompt = VlmPromptBuilder.Build();
        var requestBody = new
        {
            model = modelTag,
            prompt = prompt,
            images = new[] { Convert.ToBase64String(imageBytes) },
            stream = false,
            options = new { num_ctx = numCtx, temperature = temperature },
        };

        using var client = clientFactory.CreateClient(OllamaClientNames.Vlm);
        var generateUri = new Uri(BaseAddress(client), "/api/generate");
        using var req = new HttpRequestMessage(HttpMethod.Post, generateUri)
        {
            Content = JsonContent.Create(requestBody),
        };
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var responseBytes = await resp.Content.ReadAsByteArrayAsync(ct);

        OllamaGenerateResponse? outer;
        try
        {
            outer = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseBytes, GenerateResponseJson);
        }
        catch (JsonException ex)
        {
            throw new OllamaJsonParseException(
                System.Text.Encoding.UTF8.GetString(responseBytes),
                "Failed to deserialize outer Ollama response", ex);
        }
        if (outer is null || string.IsNullOrWhiteSpace(outer.Response))
        {
            return Skipped("vision_empty_response", srcWidth, srcHeight, exif, phashHex, blurScore);
        }

        var description = outer.Response.Trim();
        var extractedText = $"Description:\n{description}";

        var cacheKey = ExtractionCacheKeys.ForOllama(modelTag);
        var extra = BuildExtra(srcWidth, srcHeight, exif, phashHex, blurScore, outer);

        var evalMs = outer.EvalDuration / 1_000_000.0;
        LogSucceeded(log, modelTag, evalMs, srcWidth, srcHeight);

        return new VlmExtractionOutcome(
            ExtractedText: extractedText,
            ExtractionCacheKey: cacheKey,
            Extra: extra,
            Skipped: false,
            SkipReason: null);
    }

    private async Task<byte[]> ResizeViaFfmpegAsync(
        byte[] sourceBytes, int maxEdge, int qscale, TimeSpan timeout, CancellationToken ct)
    {
        var inPath  = Path.Combine(Path.GetTempPath(), $"vlm-in-{Guid.NewGuid():N}");
        var outPath = Path.Combine(Path.GetTempPath(), $"vlm-out-{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(inPath, sourceBytes, ct);
            var filter = $"scale=w='min(iw,{maxEdge})':h='min(ih,{maxEdge})':force_original_aspect_ratio=decrease";
            var args = $"-y -i \"{inPath}\" -vf \"{filter}\" -q:v {qscale} \"{outPath}\"";
            await ffmpeg.RunAsync(args, timeout, ct);
            var resized = await File.ReadAllBytesAsync(outPath, ct);
            LogResized(log, sourceBytes.Length, resized.Length, maxEdge, qscale);
            return resized;
        }
        finally
        {
            try { if (File.Exists(inPath))  File.Delete(inPath);  } catch { /* best effort */ }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* best effort */ }
        }
    }

    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var dl = clientFactory.CreateClient(OllamaClientNames.Vlm);
        using var resp = await dl.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static Uri BaseAddress(HttpClient client) =>
        client.BaseAddress ?? throw new InvalidOperationException(
            $"HttpClient '{OllamaClientNames.Vlm}' has no BaseAddress configured");

    private static VlmExtractionOutcome Skipped(
        string reason, int width, int height,
        Dictionary<string, JsonElement>? exif,
        string? phash, double? blurScore)
    {
        var extra = BuildExtra(width, height, exif, phash, blurScore, generateResponse: null);
        return new VlmExtractionOutcome(
            ExtractedText: null,
            ExtractionCacheKey: null,
            Extra: extra,
            Skipped: true,
            SkipReason: reason);
    }

    private static JsonDocument BuildExtra(
        int width, int height,
        Dictionary<string, JsonElement>? exif,
        string? phash, double? blurScore,
        OllamaGenerateResponse? generateResponse)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("exif");
            writer.WriteStartObject();
            if (exif is not null)
            {
                foreach (var (k, v) in exif) v.WriteAsProperty(writer, k);
            }
            writer.WriteEndObject();

            if (phash is not null) writer.WriteString("phash", phash);
            else writer.WriteNull("phash");

            writer.WritePropertyName("dimensions");
            writer.WriteStartObject();
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteEndObject();

            if (blurScore is not null) writer.WriteNumber("blur_score", blurScore.Value);
            else writer.WriteNull("blur_score");

            writer.WritePropertyName("model_response");
            if (generateResponse is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("model", generateResponse.Model);
                writer.WriteString("response", generateResponse.Response);
                writer.WriteBoolean("done", generateResponse.Done);
                if (generateResponse.DoneReason is not null)
                    writer.WriteString("done_reason", generateResponse.DoneReason);
                writer.WriteNumber("eval_count", generateResponse.EvalCount);
                writer.WriteNumber("eval_duration", generateResponse.EvalDuration);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    private static Dictionary<string, JsonElement>? ReadExif(ExifProfile? profile)
    {
        if (profile is null) return null;
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        TryAddString(profile, ExifTag.Make, "camera_make", dict);
        TryAddString(profile, ExifTag.Model, "camera_model", dict);
        TryAddString(profile, ExifTag.DateTimeOriginal, "taken_at", dict);
        TryAddUInt16(profile, ExifTag.Orientation, "orientation", dict);
        TryAddGps(profile, dict);

        return dict.Count == 0 ? null : dict;
    }

    private static void TryAddString(
        ExifProfile profile, ExifTag<string> tag, string key,
        Dictionary<string, JsonElement> dict)
    {
        if (profile.TryGetValue(tag, out var v) && v?.Value is string s && !string.IsNullOrWhiteSpace(s))
        {
            dict[key] = JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
        }
    }

    private static void TryAddUInt16(
        ExifProfile profile, ExifTag<ushort> tag, string key,
        Dictionary<string, JsonElement> dict)
    {
        if (profile.TryGetValue(tag, out var v) && v is not null)
        {
            dict[key] = JsonDocument.Parse(v.Value.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
        }
    }

    private static void TryAddGps(
        ExifProfile profile, Dictionary<string, JsonElement> dict)
    {
        double? lat = TryReadRational(profile, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef);
        double? lng = TryReadRational(profile, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef);
        dict["gps_lat"] = lat.HasValue
            ? JsonDocument.Parse(lat.Value.ToString("R", CultureInfo.InvariantCulture)).RootElement.Clone()
            : JsonDocument.Parse("null").RootElement.Clone();
        dict["gps_lng"] = lng.HasValue
            ? JsonDocument.Parse(lng.Value.ToString("R", CultureInfo.InvariantCulture)).RootElement.Clone()
            : JsonDocument.Parse("null").RootElement.Clone();
    }

    private static double? TryReadRational(
        ExifProfile profile, ExifTag<SixLabors.ImageSharp.Rational[]> tag, ExifTag<string> refTag)
    {
        if (!profile.TryGetValue(tag, out var v) || v?.Value is null || v.Value.Length < 3) return null;
        var rats = v.Value;
        var deg = (double)rats[0].Numerator / rats[0].Denominator;
        var min = (double)rats[1].Numerator / rats[1].Denominator;
        var sec = (double)rats[2].Numerator / rats[2].Denominator;
        var dec = deg + min / 60.0 + sec / 3600.0;
        if (profile.TryGetValue(refTag, out var refV) && refV?.Value is string r &&
            (r.StartsWith('S') || r.StartsWith('W')))
        {
            dec = -dec;
        }
        return dec;
    }

    private static double ComputeLaplacianVariance(Image<Rgba32> image)
    {
        using var clone = image.Clone();
        var longEdge = Math.Max(clone.Width, clone.Height);
        if (longEdge > 256)
        {
            var scale = 256.0 / longEdge;
            clone.Mutate(x => x.Resize((int)(clone.Width * scale), (int)(clone.Height * scale)));
        }
        clone.Mutate(x => x.Grayscale());

        var w = clone.Width;
        var h = clone.Height;
        var gray = new double[w, h];
        clone.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    gray[x, y] = row[x].R;
                }
            }
        });

        var lap = new double[w, h];
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                lap[x, y] = -4 * gray[x, y]
                    + gray[x - 1, y] + gray[x + 1, y]
                    + gray[x, y - 1] + gray[x, y + 1];
            }
        }

        double mean = 0;
        var count = (w - 2) * (h - 2);
        if (count <= 0) return 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
                mean += lap[x, y];
        mean /= count;

        double variance = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var d = lap[x, y] - mean;
                variance += d * d;
            }
        return variance / count;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "OllamaVlmClient succeeded: model={Model} eval_ms={EvalMs} dims={Width}x{Height}")]
    private static partial void LogSucceeded(ILogger logger, string model, double evalMs, int width, int height);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "OllamaVlmClient resized via ffmpeg: bytes {SrcBytes} -> {DstBytes} (maxEdge={MaxEdge} qscale={Qscale})")]
    private static partial void LogResized(
        ILogger logger, int srcBytes, int dstBytes, int maxEdge, int qscale);
}

internal static class JsonElementWriteExtensions
{
    public static void WriteAsProperty(this JsonElement value, Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }
}
