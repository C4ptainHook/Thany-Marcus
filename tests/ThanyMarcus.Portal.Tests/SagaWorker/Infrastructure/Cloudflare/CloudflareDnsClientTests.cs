using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Infrastructure.Cloudflare;

public sealed class CloudflareDnsClientTests
{
    private const string Zone = "zone-1";
    private const string Token = "test-token";
    private const string Subdomain = "abc123";

    [Fact]
    public async Task CreateA_returns_record_on_happy_path()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldEndWith($"/zones/{Zone}/dns_records");
            return Ok("""{"success":true,"errors":[],"result":{"id":"rec-1","name":"abc123","content":"203.0.113.1"}}""");
        });

        var client = BuildClient(handler);

        var result = await client.CreateAAsync(Subdomain, IPAddress.Parse("203.0.113.1"), string.Empty, TestContext.Current.CancellationToken);

        result.Id.ShouldBe("rec-1");
        result.Subdomain.ShouldBe(Subdomain);
        result.Ip.ToString().ShouldBe("203.0.113.1");
        handler.RemainingScripts.ShouldBe(0);
    }

    [Fact]
    public async Task CreateA_reuses_existing_record_and_patches_ip_on_81057()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            return Error(HttpStatusCode.BadRequest, code: 81057, message: "record already exists");
        });
        handler.Enqueue(req =>
        {
            req.Method.ShouldBe(HttpMethod.Get);
            req.RequestUri!.Query.ShouldContain("type=A");
            req.RequestUri.Query.ShouldContain($"name={Subdomain}");
            return Ok("""{"success":true,"errors":[],"result":[{"id":"rec-existing","name":"abc123","content":"198.51.100.1"}]}""");
        });
        handler.Enqueue(req =>
        {
            req.Method.ShouldBe(HttpMethod.Patch);
            req.RequestUri!.AbsolutePath.ShouldEndWith("/zones/zone-1/dns_records/rec-existing");
            var body = req.Content!.ReadAsStringAsync().Result;
            body.ShouldContain("203.0.113.99");
            body.ShouldNotContain("ttl");
            return Ok("""{"success":true,"errors":[],"result":{"id":"rec-existing","name":"abc123","content":"203.0.113.99"}}""");
        });

        var client = BuildClient(handler);

        var result = await client.CreateAAsync(Subdomain, IPAddress.Parse("203.0.113.99"), string.Empty, TestContext.Current.CancellationToken);

        result.Id.ShouldBe("rec-existing");
        result.Subdomain.ShouldBe(Subdomain);
        result.Ip.ToString().ShouldBe("203.0.113.99");
        handler.RemainingScripts.ShouldBe(0);
    }

    [Fact]
    public async Task CreateA_throws_when_81057_patch_fails()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Error(HttpStatusCode.BadRequest, code: 81057, message: "record already exists"));
        handler.Enqueue(_ => Ok("""{"success":true,"errors":[],"result":[{"id":"rec-existing","name":"abc123","content":"198.51.100.1"}]}"""));
        handler.Enqueue(_ => Error(HttpStatusCode.BadRequest, code: 9999, message: "patch refused"));

        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<CloudflareApiException>(() =>
            client.CreateAAsync(Subdomain, IPAddress.Parse("203.0.113.99"), string.Empty, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("81057 reuse");
        ex.Message.ShouldContain("9999");
        ex.Message.ShouldContain("rec-existing");
    }

    [Fact]
    public async Task CreateA_throws_with_error_code_for_non_81057_failure()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(_ => Error(HttpStatusCode.Forbidden, code: 10000, message: "auth failed"));

        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<CloudflareApiException>(() =>
            client.CreateAAsync(Subdomain, IPAddress.Parse("203.0.113.1"), string.Empty, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("10000");
        ex.Message.ShouldContain("auth failed");
    }

    private static CloudflareDnsClient BuildClient(ScriptedHandler handler)
    {
        var options = Options.Create(new CloudflareOptions
        {
            ZoneId = Zone,
            ApiToken = Token,
        });
        var factory = new ScriptedHttpFactory(handler);
        return new CloudflareDnsClient(factory, options, NullLogger<CloudflareDnsClient>.Instance);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, int code, string message)
    {
        var body = $$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"result":null}""";
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ScriptedHttpFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scripts = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> script) => _scripts.Enqueue(script);

        public int RemainingScripts => _scripts.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_scripts.Count == 0)
                throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
            var script = _scripts.Dequeue();
            request.Headers.Authorization.ShouldNotBeNull();
            request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
            request.Headers.Authorization.Parameter.ShouldBe(Token);
            return Task.FromResult(script(request));
        }
    }
}
