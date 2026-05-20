using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class ParakeetHttpClientTests : IDisposable
{
    private const string AudioKey = "notes/n/att/a.wav";
    private static readonly byte[] AudioBytes = Encoding.UTF8.GetBytes("RIFF....fake-wav-bytes");

    private readonly WireMockServer wm = WireMockServer.Start();
    private readonly FakeArtifactStore store = new();
    private readonly StubFfprobeRunner ffprobe = new(durationSeconds: 5.0, audioCodec: "pcm_s16le");
    private readonly StubFfmpegRunner ffmpeg = new();

    public ParakeetHttpClientTests()
    {
        store.SeedBody(AudioKey, AudioBytes, mimeType: "audio/wav");
    }

    [Fact]
    public async Task Happy_path_posts_audio_bytes_as_multipart_file_part_and_returns_transcript()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody(JsonSerializer.Serialize(new { text = "hello world", language = "en" })));

        var client = BuildClient();
        var result = await client.TranscribeAsync(AudioKey, ct);

        result.Text.ShouldBe("hello world");
        result.LanguageDetected.ShouldBe("en");

        var calls = wm.FindLogEntries(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost());
        calls.Count.ShouldBe(1);

        var req = calls[0].RequestMessage;
        req.Headers!["Content-Type"].ToString().ShouldStartWith("multipart/form-data");
        var body = Encoding.UTF8.GetString(req.BodyAsBytes ?? Array.Empty<byte>());
        body.ShouldContain("name=file");
        body.ShouldContain("filename=a.wav");
        body.ShouldContain(Encoding.UTF8.GetString(AudioBytes));
        body.ShouldNotContain("fake.example.test");

        ffmpeg.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Missing_language_returns_null_LanguageDetected()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithBody(JsonSerializer.Serialize(new { text = "ok" })));

        var client = BuildClient();
        var result = await client.TranscribeAsync(AudioKey, ct);

        result.Text.ShouldBe("ok");
        result.LanguageDetected.ShouldBeNull();
    }

    [Fact]
    public async Task Server_5xx_throws_HttpRequestException()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.ServiceUnavailable));

        var client = BuildClient();
        await Should.ThrowAsync<HttpRequestException>(() => client.TranscribeAsync(AudioKey, ct));
    }

    [Fact]
    public async Task Server_4xx_throws_ParakeetClientException()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.BadRequest));

        var client = BuildClient();
        var ex = await Should.ThrowAsync<ParakeetClientException>(() => client.TranscribeAsync(AudioKey, ct));
        ex.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Long_audio_is_chunked_via_ffmpeg_and_transcripts_are_concatenated()
    {
        var ct = TestContext.Current.CancellationToken;
        ffprobe.DurationSeconds = 75.0;

        ffmpeg.OnRun = (args, _) =>
        {
            ExtractSegmentTargetDir(args, out var dir, out var pattern);
            File.WriteAllBytes(Path.Combine(dir, FormatChunk(pattern, 0)), [0x01]);
            File.WriteAllBytes(Path.Combine(dir, FormatChunk(pattern, 1)), [0x02]);
            File.WriteAllBytes(Path.Combine(dir, FormatChunk(pattern, 2)), [0x03]);
        };

        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody(JsonSerializer.Serialize(new { text = "piece", language = "en" })));

        var client = BuildClient();
        var result = await client.TranscribeAsync(AudioKey, ct);

        result.Text.ShouldBe("piece piece piece");
        result.LanguageDetected.ShouldBe("en");

        ffmpeg.Calls.Count.ShouldBe(1);
        ffmpeg.Calls[0].ShouldContain("-segment_time 30");
        ffmpeg.Calls[0].ShouldContain("-ar 16000");
        ffmpeg.Calls[0].ShouldContain("-ac 1");

        var calls = wm.FindLogEntries(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost());
        calls.Count.ShouldBe(3);
        foreach (var call in calls)
        {
            var body = Encoding.UTF8.GetString(call.RequestMessage.BodyAsBytes ?? Array.Empty<byte>());
            body.ShouldContain("filename=chunk-");
        }
    }

    [Fact]
    public async Task Chunked_path_propagates_4xx_on_first_chunk_as_ParakeetClientException()
    {
        var ct = TestContext.Current.CancellationToken;
        ffprobe.DurationSeconds = 75.0;
        ffmpeg.OnRun = (args, _) =>
        {
            ExtractSegmentTargetDir(args, out var dir, out var pattern);
            File.WriteAllBytes(Path.Combine(dir, FormatChunk(pattern, 0)), [0x01]);
            File.WriteAllBytes(Path.Combine(dir, FormatChunk(pattern, 1)), [0x02]);
        };
        wm.Given(Request.Create().WithPath("/v1/audio/transcriptions").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.BadRequest));

        var client = BuildClient();
        var ex = await Should.ThrowAsync<ParakeetClientException>(() => client.TranscribeAsync(AudioKey, ct));
        ex.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Chunked_path_throws_when_ffmpeg_produces_no_segments()
    {
        var ct = TestContext.Current.CancellationToken;
        ffprobe.DurationSeconds = 75.0;
        ffmpeg.OnRun = (_, _) => { };

        var client = BuildClient();
        var ex = await Should.ThrowAsync<ParakeetClientException>(() => client.TranscribeAsync(AudioKey, ct));
        ex.Message.ShouldContain("no chunks");
    }

    private static string FormatChunk(string pattern, int index) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, index);

    private static void ExtractSegmentTargetDir(string args, out string dir, out string pattern)
    {
        var lastQuote = args.LastIndexOf('"');
        var prevQuote = args.LastIndexOf('"', lastQuote - 1);
        var fullPath = args.Substring(prevQuote + 1, lastQuote - prevQuote - 1);
        dir = Path.GetDirectoryName(fullPath)!;
        pattern = Path.GetFileName(fullPath).Replace("%03d", "{0:D3}");
    }

    private ParakeetHttpClient BuildClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(ParakeetHttpClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri(wm.Url!);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        var sp = services.BuildServiceProvider();
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var options = new ParakeetOptions
        {
            BaseUrl = wm.Url!,
            TranscribePath = "/v1/audio/transcriptions",
            MaxChunkSeconds = 30,
        };
        return new ParakeetHttpClient(
            clientFactory, store, ffmpeg, ffprobe, options, NullLogger<ParakeetHttpClient>.Instance);
    }

    public void Dispose() => wm.Dispose();

    private sealed class StubFfprobeRunner : IFfprobeRunner
    {
        public double DurationSeconds { get; set; }
        public string? AudioCodec { get; set; }

        public StubFfprobeRunner(double durationSeconds, string? audioCodec)
        {
            DurationSeconds = durationSeconds;
            AudioCodec = audioCodec;
        }

        public Task<FfprobeResult> ProbeAsync(string inputPath, CancellationToken ct) =>
            Task.FromResult(new FfprobeResult(
                DurationSeconds, null, AudioCodec, 16000, 1, null, null));

        public Task<FfprobeResult> ProbeUrlAsync(string url, CancellationToken ct) =>
            ProbeAsync(url, ct);
    }

    private sealed class StubFfmpegRunner : IFfmpegRunner
    {
        public List<string> Calls { get; } = [];
        public Action<string, CancellationToken>? OnRun { get; set; }

        public Task<FfmpegResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(arguments);
            OnRun?.Invoke(arguments, ct);
            return Task.FromResult(new FfmpegResult(0, "", ""));
        }
    }
}
