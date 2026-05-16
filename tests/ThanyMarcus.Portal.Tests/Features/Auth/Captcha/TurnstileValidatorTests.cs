using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class TurnstileValidatorTests
{
    private const string ValidSiteKey = "1x00000000000000000000AA";
    private const string ValidSecret  = "1x0000000000000000000000000000000AA";

    private static TurnstileValidator NewValidator(StubHandler handler, TurnstileOptions opts) =>
        new(new HttpClient(handler), Options.Create(opts), NullLogger<TurnstileValidator>.Instance);

    private static TurnstileOptions EnabledOptions(string? siteVerifyUrl = null) => new()
    {
        SiteKey       = ValidSiteKey,
        SecretKey     = ValidSecret,
        SiteVerifyUrl = siteVerifyUrl ?? "https://challenges.cloudflare.com/turnstile/v0/siteverify",
    };

    [Fact]
    public async Task Returns_Disabled_without_HTTP_call_when_options_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler();
        var validator = NewValidator(handler, new TurnstileOptions());

        var result = await validator.VerifyAsync("token", remoteIp: null, ct);

        result.ShouldBe(TurnstileVerifyResult.Disabled);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Returns_Empty_without_HTTP_call_when_token_is_blank()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler();
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("   ", remoteIp: null, ct);

        result.Success.ShouldBeFalse();
        result.ErrorCodes.ShouldContain("missing-input-response");
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Parses_success_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => JsonResponse(@"{""success"": true, ""error-codes"": []}"),
        };
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("good-token", remoteIp: "192.0.2.10", ct);

        result.Success.ShouldBeTrue();
        result.ErrorCodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Parses_failure_response_with_error_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => JsonResponse(
                @"{""success"": false, ""error-codes"": [""invalid-input-response""]}"),
        };
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("bad-token", remoteIp: null, ct);

        result.Success.ShouldBeFalse();
        result.ErrorCodes.ShouldContain("invalid-input-response");
    }

    [Fact]
    public async Task Posts_form_encoded_payload_with_secret_response_and_remoteip()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => JsonResponse(@"{""success"": true, ""error-codes"": []}"),
        };
        var validator = NewValidator(handler, EnabledOptions());

        await validator.VerifyAsync("the-token", remoteIp: "203.0.113.7", ct);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .ShouldBe("https://challenges.cloudflare.com/turnstile/v0/siteverify");
        handler.LastContentType.ShouldBe("application/x-www-form-urlencoded");
        var form = ParseForm(handler.LastBody!);
        form["secret"].ShouldBe(ValidSecret);
        form["response"].ShouldBe("the-token");
        form["remoteip"].ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task Omits_remoteip_when_remoteIp_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => JsonResponse(@"{""success"": true, ""error-codes"": []}"),
        };
        var validator = NewValidator(handler, EnabledOptions());

        await validator.VerifyAsync("the-token", remoteIp: null, ct);

        var form = ParseForm(handler.LastBody!);
        form.ContainsKey("remoteip").ShouldBeFalse();
    }

    [Fact]
    public async Task Fails_open_when_HttpRequestException_is_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => throw new HttpRequestException("network down"),
        };
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("token", remoteIp: null, ct);

        result.Success.ShouldBeTrue();
        result.ErrorCodes.ShouldContain("transport-error");
    }

    [Fact]
    public async Task Fails_open_when_response_is_5xx()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("token", remoteIp: null, ct);

        result.Success.ShouldBeTrue();
        result.ErrorCodes.ShouldContain("transport-error");
    }

    [Fact]
    public async Task Fails_open_when_response_body_is_invalid_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler
        {
            ResponseFactory = _ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
                };
                return resp;
            },
        };
        var validator = NewValidator(handler, EnabledOptions());

        var result = await validator.VerifyAsync("token", remoteIp: null, ct);

        result.Success.ShouldBeTrue();
        result.ErrorCodes.ShouldContain("transport-error");
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return resp;
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        return body.Split('&')
            .Where(p => p.Length > 0)
            .Select(p => p.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentType = request.Content.Headers.ContentType is { } ct
                    ? new MediaTypeHeaderValue(ct.MediaType!).MediaType
                    : null;
            }
            return ResponseFactory(request);
        }
    }
}
