using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
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
        };
        return new ParakeetHttpClient(clientFactory, store, options, NullLogger<ParakeetHttpClient>.Instance);
    }

    public void Dispose() => wm.Dispose();
}
