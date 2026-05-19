using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class OllamaVlmClientTests : IDisposable
{
    private readonly WireMockServer wm = WireMockServer.Start();

    [Fact]
    public async Task Happy_path_returns_canonical_text_and_cache_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var imageBytes = MakeJpeg(640, 480);
        wm.Given(Request.Create().WithPath("/img/test.jpg").UsingGet())
          .RespondWith(Response.Create()
              .WithStatusCode(200)
              .WithHeader("Content-Type", "image/jpeg")
              .WithBody(imageBytes));

        var ollamaInner = JsonSerializer.Serialize(new { description = "A cat sitting on a mat", text_in_image = (string?)null });
        var ollamaOuter = JsonSerializer.Serialize(new
        {
            model = "openbmb/minicpm-v4.6:q4_K_M",
            response = ollamaInner,
            done = true,
            done_reason = "stop",
            eval_count = 100,
            eval_duration = 5_000_000_000L,
        });
        wm.Given(Request.Create().WithPath("/api/generate").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody(ollamaOuter));

        var client = BuildClient();
        var att = MakeAttachment(storageProvider: "test", url: null);

        var outcome = await client.ExtractAsync(att, ct);

        outcome.Skipped.ShouldBeFalse();
        outcome.ExtractedText.ShouldStartWith("Description:\nA cat sitting on a mat");
        outcome.ExtractedText.ShouldNotContain("Text:");
        outcome.ExtractionCacheKey.ShouldBe("sha256:ollama:openbmb-minicpm-v4.6-q4_K_M");

        using var extra = outcome.Extra;
        extra.RootElement.GetProperty("phash").GetString().ShouldNotBeNullOrEmpty();
        extra.RootElement.GetProperty("dimensions").GetProperty("width").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Description_with_text_in_image_is_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/img/test.jpg").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "image/jpeg")
              .WithBody(MakeJpeg(640, 480)));

        var inner = JsonSerializer.Serialize(new { description = "A poster", text_in_image = "SALE 50%" });
        wm.Given(Request.Create().WithPath("/api/generate").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithBody(JsonSerializer.Serialize(new { model = "m", response = inner, done = true, eval_count = 1L, eval_duration = 1L })));

        var client = BuildClient();
        var outcome = await client.ExtractAsync(MakeAttachment(), ct);

        outcome.ExtractedText.ShouldBe("Description:\nA poster\n\nText:\nSALE 50%");
    }

    [Fact]
    public async Task Non_json_inner_response_throws_parse_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/img/test.jpg").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "image/jpeg")
              .WithBody(MakeJpeg(640, 480)));

        wm.Given(Request.Create().WithPath("/api/generate").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithBody(JsonSerializer.Serialize(new { model = "m", response = "not-a-json-payload", done = true, eval_count = 0L, eval_duration = 0L })));

        var client = BuildClient();
        await Should.ThrowAsync<OllamaJsonParseException>(
            async () => await client.ExtractAsync(MakeAttachment(), ct));
    }

    [Fact]
    public async Task Dimension_under_min_skips_without_calling_ollama()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/img/test.jpg").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "image/jpeg")
              .WithBody(MakeJpeg(50, 50)));

        wm.Given(Request.Create().WithPath("/api/generate").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(500));

        var client = BuildClient();
        var outcome = await client.ExtractAsync(MakeAttachment(), ct);

        outcome.Skipped.ShouldBeTrue();
        outcome.SkipReason.ShouldBe("dimensions_out_of_range:50x50");
        outcome.ExtractedText.ShouldBeNull();
        outcome.ExtractionCacheKey.ShouldBeNull();

        // Ollama should not have been hit.
        var generateCalls = wm.FindLogEntries(Request.Create().WithPath("/api/generate"));
        generateCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Ollama_500_throws_for_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/img/test.jpg").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "image/jpeg")
              .WithBody(MakeJpeg(640, 480)));

        wm.Given(Request.Create().WithPath("/api/generate").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.ServiceUnavailable));

        var client = BuildClient();
        await Should.ThrowAsync<HttpRequestException>(
            async () => await client.ExtractAsync(MakeAttachment(), ct));
    }

    private OllamaVlmClient BuildClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(OllamaClientNames.Vlm, c =>
        {
            c.BaseAddress = new Uri(wm.Url!);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        var sp = services.BuildServiceProvider();

        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var store = new FakeStoreReturningWireMockUrl(wm.Url!);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IngestSaga:Sidecars:Ollama:BaseUrl"] = wm.Url,
            ["IngestSaga:Models:Vlm:OllamaTag"] = "openbmb/minicpm-v4.6:q4_K_M",
            ["IngestSaga:Filters:Image:MinDimension"] = "100",
            ["IngestSaga:Filters:Image:MaxDimension"] = "16384",
        }).Build();
        return new OllamaVlmClient(clientFactory, store, config, NullLogger<OllamaVlmClient>.Instance);
    }

    private static Attachment MakeAttachment(string storageProvider = "test", string? url = null) => new()
    {
        Id = Guid.CreateVersion7(),
        NoteId = Guid.CreateVersion7(),
        ClientAttachmentId = "a",
        Kind = AttachmentKind.Image,
        StorageProvider = storageProvider,
        StorageBucket = "test",
        StorageKey = "img/test.jpg",
        Sha256 = "abc",
        Url = url,
        Status = AttachmentStatus.Uploaded,
        ExtractionStatus = AttachmentExtractionStatus.Pending,
        Extra = JsonDocument.Parse("{}"),
        CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
    };

    private static byte[] MakeJpeg(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                img[x, y] = new Rgba32((byte)((x * 13 + y * 7) % 256), (byte)((x * 5 + y * 11) % 256), 100, 255);
            }
        }
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 75 });
        return ms.ToArray();
    }

    public void Dispose() => wm.Dispose();

    private sealed class FakeStoreReturningWireMockUrl(string baseUrl) : IArtifactStore
    {
        public Task<PresignedDownload> IssueDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct) =>
            Task.FromResult(new PresignedDownload(
                new Uri($"{baseUrl}/img/test.jpg"),
                SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromTimeSpan(ttl))));

        public Task<PresignedUpload> IssueUploadUrlAsync(string key, string mimeType, long byteSize, TimeSpan ttl, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct) =>
            Task.FromResult<ObjectMetadata?>(null);
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UploadBytesAsync(string key, byte[] bytes, string mimeType, bool finalized, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
