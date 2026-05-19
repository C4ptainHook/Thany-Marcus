using System.Net;
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

public sealed class DoclingHttpClientTests : IDisposable
{
    private readonly WireMockServer wm = WireMockServer.Start();

    [Fact]
    public async Task Happy_path_posts_presigned_url_and_returns_markdown()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1alpha/convert/source").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "application/json")
              .WithBody(JsonSerializer.Serialize(new
              {
                  document = new { md_content = "# Hello\n\nWorld", filename = "x.pdf" },
              })));

        var client = BuildClient();
        var md = await client.ExtractMarkdownAsync("notes/n1/att/a1.pdf", "application/pdf", ct);

        md.ShouldBe("# Hello\n\nWorld");
        var calls = wm.FindLogEntries(Request.Create().WithPath("/v1alpha/convert/source").UsingPost());
        calls.Count.ShouldBe(1);
        var body = calls[0].RequestMessage.Body ?? "";
        body.ShouldContain("http_source");
        body.ShouldContain("fake.example.test");
    }

    [Fact]
    public async Task Server_5xx_throws_transient_HttpRequestException()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1alpha/convert/source").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.ServiceUnavailable)
              .WithBody("upstream down"));

        var client = BuildClient();
        await Should.ThrowAsync<HttpRequestException>(
            () => client.ExtractMarkdownAsync("k", "application/pdf", ct));
    }

    [Fact]
    public async Task Server_4xx_throws_permanent_DoclingClientException()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1alpha/convert/source").UsingPost())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.BadRequest)
              .WithBody("malformed"));

        var client = BuildClient();
        var ex = await Should.ThrowAsync<DoclingClientException>(
            () => client.ExtractMarkdownAsync("k", "application/pdf", ct));
        ex.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Missing_md_content_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        wm.Given(Request.Create().WithPath("/v1alpha/convert/source").UsingPost())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithBody(JsonSerializer.Serialize(new { document = new { filename = "x.pdf" } })));

        var client = BuildClient();
        await Should.ThrowAsync<DoclingClientException>(
            () => client.ExtractMarkdownAsync("k", "application/pdf", ct));
    }

    private DoclingHttpClient BuildClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(DoclingHttpClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri(wm.Url!);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        var sp = services.BuildServiceProvider();
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var store = new FakeArtifactStore();
        var options = new DoclingOptions { BaseUrl = wm.Url!, ConvertSourcePath = "/v1alpha/convert/source" };
        return new DoclingHttpClient(clientFactory, store, options, NullLogger<DoclingHttpClient>.Instance);
    }

    public void Dispose() => wm.Dispose();
}
