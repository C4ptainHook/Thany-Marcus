using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.RateLimiting;

public sealed class SignInGoogleRateLimitTests(PostgresFixture postgres) : FactoryTestBase(postgres)
{
    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    [Fact]
    public async Task Twenty_first_request_returns_429_with_body_and_retry_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory.WithRemoteIpHeader();
        using var client = factory.CreateClient(NoRedirect);

        for (var i = 0; i < 20; i++)
        {
            var response = await SendCallbackAsync(client, "192.0.2.10", ct);
            response.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        var rejected = await SendCallbackAsync(client, "192.0.2.10", ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        rejected.Headers.TryGetValues("Retry-After", out var retryHeader).ShouldBeTrue();
        int.Parse(retryHeader!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

        var body = await rejected.Content.ReadFromJsonAsync<RateLimitedBody>(ct);
        body!.Error.ShouldBe("rate_limited");
        body.RetryAfterSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Partition_is_per_ip_so_other_ip_first_call_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory.WithRemoteIpHeader();
        using var client = factory.CreateClient(NoRedirect);

        for (var i = 0; i < 20; i++)
        {
            var response = await SendCallbackAsync(client, "192.0.2.10", ct);
            response.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
        (await SendCallbackAsync(client, "192.0.2.10", ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var otherIp = await SendCallbackAsync(client, "192.0.2.11", ct);
        otherIp.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> SendCallbackAsync(HttpClient client, string ip, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/signin-google?code=test&state=test");
        req.Headers.Add(RateLimitTestExtensions.RemoteIpHeader, ip);
        return client.SendAsync(req, ct);
    }

    private sealed record RateLimitedBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("retry_after_seconds")] int RetryAfterSeconds);
}
