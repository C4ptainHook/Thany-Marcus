using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Extraction;

public sealed class UrlExtractorTests : IAsyncLifetime
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

    private UrlExtractor NewExtractor() =>
        new(clientFactory, NullLogger<UrlExtractor>.Instance);

    [Fact]
    public async Task Og_only_url_returns_meta_head_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/og").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "text/html; charset=utf-8")
              .WithBody("""
                <html><head>
                  <title>Page Title</title>
                  <meta property="og:title" content="Better Title" />
                  <meta property="og:description" content="Page description" />
                  <meta property="og:site_name" content="Example" />
                </head></html>
                """));

        var result = await NewExtractor().ExtractAsync($"{wm.Url}/og", ct);

        result.MinimalReason.ShouldBeNull();
        result.HttpStatus.ShouldBe(200);
        result.Title.ShouldBe("Better Title");
        result.Description.ShouldBe("Page description");
        result.ProviderName.ShouldBe("Example");
        result.AuthorName.ShouldBeNull();
    }

    [Fact]
    public async Task Http_404_returns_minimal()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/missing").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404));

        var result = await NewExtractor().ExtractAsync($"{wm.Url}/missing", ct);

        result.MinimalReason.ShouldBe("http: 404");
        result.Title.ShouldBeNull();
    }

    [Fact]
    public async Task Pdf_content_type_returns_minimal()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/doc.pdf").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/pdf")
              .WithBody("not-really-a-pdf"));

        var result = await NewExtractor().ExtractAsync($"{wm.Url}/doc.pdf", ct);

        result.MinimalReason.ShouldNotBeNull();
        result.MinimalReason!.ShouldStartWith("content-type:");
    }

    [Fact]
    public async Task Dns_failure_returns_minimal()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await NewExtractor().ExtractAsync(
            "https://nonexistent-thany-test-domain-12345.invalid/", ct);

        result.MinimalReason.ShouldNotBeNull();
        result.MinimalReason!.ShouldStartWith("unreachable");
    }

    [Fact]
    public async Task Malformed_url_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await NewExtractor().ExtractAsync("not-a-url", ct));
    }

    [Fact]
    public async Task Oembed_discovered_uses_oembed_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/video").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "text/html; charset=utf-8")
              .WithBody($"""
                <html><head>
                  <title>page</title>
                  <link rel="alternate" type="application/json+oembed" href="{wm.Url}/oembed" />
                </head></html>
                """));
        wm.Given(Request.Create().WithPath("/oembed").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody("""
                {
                  "title": "Cool Video",
                  "author_name": "A Creator",
                  "provider_name": "FakeTube",
                  "thumbnail_url": "https://cdn.example/thumb.jpg",
                  "type": "video"
                }
                """));

        var result = await NewExtractor().ExtractAsync($"{wm.Url}/video", ct);

        result.MinimalReason.ShouldBeNull();
        result.Title.ShouldBe("Cool Video");
        result.AuthorName.ShouldBe("A Creator");
        result.ProviderName.ShouldBe("FakeTube");
        result.ThumbnailUrl.ShouldBe("https://cdn.example/thumb.jpg");
    }
}
