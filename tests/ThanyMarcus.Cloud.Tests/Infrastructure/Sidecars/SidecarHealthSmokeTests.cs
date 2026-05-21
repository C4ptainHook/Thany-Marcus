using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Shouldly;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class SidecarHealthSmokeTests : IAsyncLifetime
{
    private static readonly string[] InstallCurl = ["apk", "add", "--no-cache", "curl"];

    private INetwork _network = null!;
    private IContainer _ollamaVision = null!;
    private IContainer _ollamaText = null!;
    private IContainer _docling = null!;
    private IContainer _parakeet = null!;
    private IContainer _cloudApi = null!;

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder()
            .WithName($"cloud-sidecars-test-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync();

        _ollamaVision = BuildMock("ollama-vision", "11434", "{\"version\":\"mock\"}");
        _ollamaText   = BuildMock("ollama-text",   "11434", "{\"version\":\"mock\"}");
        _docling      = BuildMock("docling",       "5001",  "ok");
        _parakeet     = BuildMock("parakeet",      "5092",  "{\"status\":\"ok\"}");

        await Task.WhenAll(
            _ollamaVision.StartAsync(),
            _ollamaText.StartAsync(),
            _docling.StartAsync(),
            _parakeet.StartAsync());

        _cloudApi = new ContainerBuilder("alpine:3.20")
            .WithNetwork(_network)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand("sleep 3600")
            .Build();
        await _cloudApi.StartAsync();

        var ct = TestContext.Current.CancellationToken;
        var install = await _cloudApi.ExecAsync(InstallCurl, ct);
        install.ExitCode.ShouldBe(0L, $"apk add curl failed: stderr={install.Stderr}");
    }

    [Fact]
    public async Task All_sidecars_are_reachable_by_service_name()
    {
        await AssertCurl("http://ollama-vision:11434/api/version", "\"version\"");
        await AssertCurl("http://ollama-text:11434/api/version",   "\"version\"");
        await AssertCurl("http://docling:5001/health",             "ok");
        await AssertCurl("http://parakeet:5092/health",            "\"status\":\"ok\"");
    }

    private IContainer BuildMock(string serviceName, string port, string body) =>
        new ContainerBuilder("hashicorp/http-echo:latest")
            .WithNetwork(_network)
            .WithNetworkAliases(serviceName)
            .WithCommand($"-listen=:{port}", $"-text={body}")
            .Build();

    private async Task AssertCurl(string url, string contains)
    {
        var ct = TestContext.Current.CancellationToken;
        string[] cmd = ["curl", "-fsS", url];
        var result = await _cloudApi.ExecAsync(cmd, ct);
        result.ExitCode.ShouldBe(0L, $"curl {url} failed: stderr={result.Stderr}");
        result.Stdout.ShouldContain(contains, Case.Sensitive, $"unexpected body from {url}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_cloudApi is not null) await _cloudApi.DisposeAsync();
        if (_ollamaVision is not null) await _ollamaVision.DisposeAsync();
        if (_ollamaText is not null) await _ollamaText.DisposeAsync();
        if (_docling is not null) await _docling.DisposeAsync();
        if (_parakeet is not null) await _parakeet.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}
