using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Specialists;

public sealed class UrlMetadataWorkerTests : IAsyncLifetime
{
    private WireMockServer wm = null!;
    private IHttpClientFactory clientFactory = null!;

    public ValueTask InitializeAsync()
    {
        wm = WireMockServer.Start();
        var services = new ServiceCollection();
        services.AddHttpClient(UrlExtractor.HttpClientName);
        services.AddLogging();
        clientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        wm.Dispose();
        return ValueTask.CompletedTask;
    }

    private UrlExtractor NewExtractor() => new(clientFactory, NullLogger<UrlExtractor>.Instance);

    [Fact]
    public void Generic_url_parses_og_tags_marks_extracted()
    {
        var result = new UrlExtractionResult(
            CanonicalUrl: "https://example.com/a",
            Title: "A Title",
            Description: "A description",
            AuthorName: null,
            ProviderName: "Example",
            ThumbnailUrl: "https://cdn/x.jpg",
            HttpStatus: 200,
            MinimalReason: null);

        var outcome = UrlMetadataWorker.MapOutcome(result);

        outcome.AttachmentStatus.ShouldBe(AttachmentExtractionStatus.Extracted);
        outcome.ExtractionError.ShouldBeNull();
        using var doc = outcome.Extra!;
        doc.RootElement.GetProperty("title").GetString().ShouldBe("A Title");
        doc.RootElement.GetProperty("thumbnail_url").GetString().ShouldBe("https://cdn/x.jpg");
    }

    [Fact]
    public void Url_without_metadata_marks_extracted_minimal()
    {
        var result = new UrlExtractionResult(
            CanonicalUrl: "https://example.com/file.pdf",
            Title: null, Description: null, AuthorName: null, ProviderName: null, ThumbnailUrl: null,
            HttpStatus: 200, MinimalReason: "content-type: application/pdf");

        var outcome = UrlMetadataWorker.MapOutcome(result);

        outcome.AttachmentStatus.ShouldBe(AttachmentExtractionStatus.ExtractedMinimal);
    }

    [Theory]
    [InlineData("unreachable: dns")]
    [InlineData("unreachable: timeout")]
    [InlineData("http: 404")]
    public void Network_or_http_failure_marks_failed(string minimalReason)
    {
        var result = new UrlExtractionResult(
            CanonicalUrl: "https://example.com/dead",
            Title: null, Description: null, AuthorName: null, ProviderName: null, ThumbnailUrl: null,
            HttpStatus: 0, MinimalReason: minimalReason);

        var outcome = UrlMetadataWorker.MapOutcome(result);

        outcome.AttachmentStatus.ShouldBe(AttachmentExtractionStatus.Failed);
        outcome.ExtractionError.ShouldBe(minimalReason);
    }

    [Fact]
    public async Task Youtube_like_url_uses_oembed_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/watch").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "text/html; charset=utf-8")
              .WithBody($"""
                <html><head>
                  <title>watch</title>
                  <link rel="alternate" type="application/json+oembed" href="{wm.Url}/oembed" />
                </head></html>
                """));
        wm.Given(Request.Create().WithPath("/oembed").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody("""
                { "title": "Cool Video", "author_name": "Channel", "provider_name": "YouTube" }
                """));

        var result = await NewExtractor().ExtractAsync($"{wm.Url}/watch", ct);
        var outcome = UrlMetadataWorker.MapOutcome(result);

        outcome.AttachmentStatus.ShouldBe(AttachmentExtractionStatus.Extracted);
        wm.LogEntries.ShouldContain(e => e.RequestMessage.Path == "/oembed");
        using var doc = outcome.Extra!;
        doc.RootElement.GetProperty("title").GetString().ShouldBe("Cool Video");
        doc.RootElement.GetProperty("provider_name").GetString().ShouldBe("YouTube");
    }
}
