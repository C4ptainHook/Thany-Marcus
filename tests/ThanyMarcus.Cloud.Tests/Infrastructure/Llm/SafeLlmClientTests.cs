using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Llm;

public sealed class SafeLlmClientTests
{
    private static SafeLlmClient NewClient(StubHandler handler, int maxAttempts = 3)
    {
        var options = new LlmIntelligenceOptions
        {
            OllamaTag = "stub-tag",
            Retry = new RetryOptions { MaxAttempts = maxAttempts },
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub.ollama.test") };
        return new SafeLlmClient(http, new TestOptionsMonitor<LlmIntelligenceOptions>(options), NullLogger<SafeLlmClient>.Instance);
    }

    [Fact]
    public async Task Returns_parsed_dto_on_first_valid_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => OllamaOk("""{"project_entity_id": null, "confidence": 0.4, "rationale": "ok"}"""));
        var client = NewClient(handler);
        var dto = await client.CompleteAsync<RouteDecisionDto>(
            new PromptId("route", "v1"), new LlmPromptRequest("prompt"), ct);
        dto.ProjectEntityId.ShouldBeNull();
        dto.Confidence.ShouldBe(0.4);
        handler.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Retries_on_malformed_then_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var responses = new Queue<string>([
            "not valid json",
            """{"project_entity_id": null, "confidence": 0.9, "rationale": "x"}""",
        ]);
        var handler = new StubHandler(_ => OllamaOk(responses.Dequeue()));
        var client = NewClient(handler);
        var dto = await client.CompleteAsync<RouteDecisionDto>(
            new PromptId("route", "v1"), new LlmPromptRequest("prompt"), ct);
        dto.Confidence.ShouldBe(0.9);
        handler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Throws_LlmStructuredOutputException_after_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => OllamaOk("not valid json"));
        var client = NewClient(handler);
        var ex = await Should.ThrowAsync<LlmStructuredOutputException>(async () =>
            await client.CompleteAsync<RouteDecisionDto>(
                new PromptId("route", "v1"), new LlmPromptRequest("prompt"), ct));
        ex.Attempts.ShouldBe(3);
        handler.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Throws_on_http_5xx()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        var client = NewClient(handler);
        await Should.ThrowAsync<LlmStructuredOutputException>(async () =>
            await client.CompleteAsync<RouteDecisionDto>(
                new PromptId("route", "v1"), new LlmPromptRequest("prompt"), ct));
    }

    [Fact]
    public async Task String_T_skips_json_parsing_and_trims()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => OllamaOk("  ## hello\n  "));
        var client = NewClient(handler);
        var s = await client.CompleteAsync<string>(
            new PromptId("hub-generate", "v1"), new LlmPromptRequest("prompt"), ct);
        s.ShouldBe("## hello");
        handler.Attempts.ShouldBe(1);
    }

    private static HttpResponseMessage OllamaOk(string responseField)
    {
        var payload = $$"""{"model":"stub","response":{{System.Text.Json.JsonSerializer.Serialize(responseField)}},"done":true,"done_reason":"stop","eval_count":1,"eval_duration":1}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public int Attempts { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Attempts += 1;
            return Task.FromResult(respond(req));
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
