using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class ParakeetHttpClient : IParakeetClient
{
    public const string HttpClientName = "Parakeet";

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory clientFactory;
    private readonly IArtifactStore store;
    private readonly IFfmpegRunner ffmpeg;
    private readonly IFfprobeRunner ffprobe;
    private readonly ParakeetOptions options;
    private readonly ILogger<ParakeetHttpClient> log;

    public ParakeetHttpClient(
        IHttpClientFactory clientFactory,
        IArtifactStore store,
        IFfmpegRunner ffmpeg,
        IFfprobeRunner ffprobe,
        ParakeetOptions options,
        ILogger<ParakeetHttpClient> log)
    {
        this.clientFactory = clientFactory;
        this.store = store;
        this.ffmpeg = ffmpeg;
        this.ffprobe = ffprobe;
        this.options = options;
        this.log = log;
    }

    public async Task<ParakeetTranscript> TranscribeAsync(string storageKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var maxChunkSeconds = Math.Max(1, options.MaxChunkSeconds);
        var tempDir = Path.Combine(Path.GetTempPath(), $"tm-parakeet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "input");
        try
        {
            await using (var src = await store.OpenReadAsync(storageKey, ct))
            await using (var dst = File.Create(inputPath))
            {
                await src.CopyToAsync(dst, ct);
            }

            FfprobeResult probe;
            try
            {
                probe = await ffprobe.ProbeAsync(inputPath, ct);
            }
            catch (FfmpegRunnerException ex)
            {
                throw new ParakeetClientException("parakeet ffprobe failed: " + ex.Message, ex);
            }

            if (probe.DurationSeconds <= maxChunkSeconds)
            {
                await using var fs = File.OpenRead(inputPath);
                return await PostOneAsync(fs, Path.GetFileName(storageKey), storageKey, ct);
            }

            var chunkPattern = Path.Combine(tempDir, "chunk-%03d.wav");
            try
            {
                await ffmpeg.RunAsync(
                    $"-y -i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le -vn " +
                    $"-f segment -segment_time {maxChunkSeconds} -reset_timestamps 1 \"{chunkPattern}\"",
                    TimeSpan.FromMinutes(5), ct);
            }
            catch (FfmpegRunnerException ex)
            {
                throw new ParakeetClientException("parakeet ffmpeg segment failed: " + ex.Message, ex);
            }

            var chunkPaths = Directory.GetFiles(tempDir, "chunk-*.wav");
            Array.Sort(chunkPaths, StringComparer.Ordinal);
            if (chunkPaths.Length == 0)
            {
                throw new ParakeetClientException("parakeet ffmpeg segment produced no chunks");
            }

            LogChunked(log, storageKey, probe.DurationSeconds, chunkPaths.Length, maxChunkSeconds);

            var combined = new StringBuilder();
            string? language = null;
            for (var i = 0; i < chunkPaths.Length; i++)
            {
                await using var fs = File.OpenRead(chunkPaths[i]);
                var part = await PostOneAsync(fs, Path.GetFileName(chunkPaths[i]), storageKey, ct);
                if (part.Text.Length > 0)
                {
                    if (combined.Length > 0) combined.Append(' ');
                    combined.Append(part.Text);
                }
                language ??= part.LanguageDetected;
            }
            return new ParakeetTranscript(combined.ToString(), language);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { LogCleanup(log, ex, tempDir); }
        }
    }

    private async Task<ParakeetTranscript> PostOneAsync(
        Stream content, string filename, string storageKeyForLog, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", filename);
        using var http = clientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, options.TranscribePath)
        {
            Content = form,
        };

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ParakeetClientException("parakeet request failed: " + ex.Message, ex);
        }
        using var _resp = resp;

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            LogFailure(log, (int)resp.StatusCode, storageKeyForLog);
            if (IsTransient(resp.StatusCode))
            {
                throw new HttpRequestException(
                    $"parakeet transient {(int)resp.StatusCode}: {Truncate(errBody)}");
            }
            throw new ParakeetClientException(
                $"parakeet returned {(int)resp.StatusCode}",
                (int)resp.StatusCode,
                Truncate(errBody));
        }

        ParakeetResponse? parsed;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<ParakeetResponse>(ResponseJson, ct);
        }
        catch (JsonException ex)
        {
            throw new ParakeetClientException("parakeet response was not valid JSON", ex);
        }
        if (parsed?.Text is null)
        {
            throw new ParakeetClientException("parakeet response missing 'text' field");
        }
        return new ParakeetTranscript(parsed.Text, parsed.Language);
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code >= 500 || code == HttpStatusCode.RequestTimeout || code == HttpStatusCode.TooManyRequests;

    private static string Truncate(string body) =>
        body.Length <= 512 ? body : body[..512] + "...";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "ParakeetHttpClient non-success: status={Status} storage_key={Key}")]
    private static partial void LogFailure(ILogger logger, int status, string key);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "ParakeetHttpClient chunked: storage_key={Key} duration_s={Duration} chunks={Count} chunk_s={ChunkSeconds}")]
    private static partial void LogChunked(ILogger logger, string key, double duration, int count, int chunkSeconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "ParakeetHttpClient temp cleanup failed: dir={Dir}")]
    private static partial void LogCleanup(ILogger logger, Exception ex, string dir);

    internal sealed record ParakeetResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);
}
