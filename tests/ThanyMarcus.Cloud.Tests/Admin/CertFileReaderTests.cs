using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Admin.Health;

namespace ThanyMarcus.Cloud.Tests.Admin;

public sealed class CertFileReaderTests
{
    [Fact]
    public void Returns_false_when_live_dir_missing()
    {
        var reader = new CertFileReader(
            ConfigWith("/tmp/cert-file-reader-does-not-exist"),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeFalse();
    }

    [Fact]
    public void Returns_true_when_fullchain_and_privkey_both_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "fullchain.pem"), "x");
        File.WriteAllText(Path.Combine(tmp.Path, "privkey.pem"),   "y");
        var reader = new CertFileReader(
            ConfigWith(tmp.Path),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeTrue();
    }

    [Fact]
    public void Returns_false_when_only_fullchain_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "fullchain.pem"), "x");
        var reader = new CertFileReader(
            ConfigWith(tmp.Path),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeFalse();
    }

    [Fact]
    public void Returns_false_when_only_privkey_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "privkey.pem"), "y");
        var reader = new CertFileReader(
            ConfigWith(tmp.Path),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeFalse();
    }

    [Fact]
    public void Throws_when_live_dir_not_configured()
    {
        var empty = new ConfigurationBuilder().Build();
        Should.Throw<InvalidOperationException>(() =>
            new CertFileReader(empty, NullLogger<CertFileReader>.Instance));
    }

    private static IConfiguration ConfigWith(string liveDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cert:LiveDir"] = liveDir })
            .Build();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cert-file-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
