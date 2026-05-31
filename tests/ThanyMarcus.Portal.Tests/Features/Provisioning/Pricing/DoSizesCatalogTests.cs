using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;

namespace ThanyMarcus.Portal.Tests.Features.Provisioning.Pricing;

public sealed class DoSizesCatalogTests
{
    [Fact]
    public async Task First_call_hits_HTTP_and_returns_matching_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler { ResponseFactory = _ => JsonResponse(SizesPayload()) };
        var catalog = NewCatalog(handler);

        var size = await catalog.GetAsync("s-4vcpu-8gb", "tok", ct);

        size.ShouldNotBeNull();
        size!.MonthlyUsd.ShouldBe(48.00m);
        size.HourlyUsd.ShouldBe(0.07143m);
        handler.RequestCount.ShouldBe(1);
        handler.LastAuthorization.ShouldBe("Bearer tok");
    }

    [Fact]
    public async Task Second_call_within_TTL_hits_cache_and_skips_HTTP()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler { ResponseFactory = _ => JsonResponse(SizesPayload()) };
        var catalog = NewCatalog(handler);

        await catalog.GetAsync("s-4vcpu-8gb", "tok", ct);
        var hit = await catalog.GetAsync("s-2vcpu-4gb", "tok", ct);

        hit.ShouldNotBeNull();
        hit!.HourlyUsd.ShouldBe(0.03571m);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task HTTP_error_returns_null_and_does_not_poison_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var catalog = NewCatalog(handler);

        var first = await catalog.GetAsync("s-4vcpu-8gb", "tok", ct);
        first.ShouldBeNull();

        handler.ResponseFactory = _ => JsonResponse(SizesPayload());
        var second = await catalog.GetAsync("s-4vcpu-8gb", "tok", ct);
        second.ShouldNotBeNull();
        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_slug_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler { ResponseFactory = _ => JsonResponse(SizesPayload()) };
        var catalog = NewCatalog(handler);

        var size = await catalog.GetAsync("nonexistent-slug", "tok", ct);

        size.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_payload_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler { ResponseFactory = _ => JsonResponse(@"{""unexpected"": true}") };
        var catalog = NewCatalog(handler);

        var size = await catalog.GetAsync("s-4vcpu-8gb", "tok", ct);

        size.ShouldBeNull();
    }

    private static DoSizesCatalog NewCatalog(StubHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new DoSizesCatalog(factory, cache, NullLogger<DoSizesCatalog>.Instance);
    }

    private static string SizesPayload() => """
        {
          "sizes": [
            { "slug": "s-2vcpu-4gb", "price_monthly": 24.0, "price_hourly": 0.03571, "memory": 4096, "vcpus": 2, "disk": 80 },
            { "slug": "s-4vcpu-8gb", "price_monthly": 48.0, "price_hourly": 0.07143, "memory": 8192, "vcpus": 4, "disk": 160 }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.digitalocean.com/"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);
        public int RequestCount { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(ResponseFactory(request));
        }
    }
}
