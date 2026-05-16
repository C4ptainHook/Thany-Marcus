using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class WorkspaceLayoutTests
{
    [Fact]
    public async Task Renders_main_tf_backend_tf_and_tfvars_for_stub_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new TempDir();
        var modulesRoot = sandbox.Path("modules");
        Directory.CreateDirectory(Path.Combine(modulesRoot, "stub"));

        var layout = new WorkspaceLayout(
            BuildConfig(sandbox.Path("workspace"), modulesRoot),
            NullLogger<WorkspaceLayout>.Instance);

        var (job, cloud) = MakeJobAndCloud("stub");

        var dir = await layout.RenderAsync(job, cloud, ct);

        dir.ShouldStartWith(sandbox.Path("workspace"));
        var mainTf = await File.ReadAllTextAsync(Path.Combine(dir, "main.tf"), ct);
        mainTf.ShouldContain("module \"cloud\"");
        mainTf.ShouldContain("output \"ip\"");
        mainTf.ShouldNotContain("required_providers");

        var backendTf = await File.ReadAllTextAsync(Path.Combine(dir, "backend.tf"), ct);
        backendTf.ShouldContain("backend \"pg\"");

        var tfvars = await File.ReadAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"), ct);
        tfvars.ShouldContain(cloud.Id.ToString());
        tfvars.ShouldContain(cloud.Hostname);
        tfvars.ShouldNotContain("provider_token");
    }

    [Fact]
    public void GetJobDir_returns_deterministic_path()
    {
        using var sandbox = new TempDir();
        var layout = new WorkspaceLayout(
            BuildConfig(sandbox.Path("workspace"), sandbox.Path("modules")),
            NullLogger<WorkspaceLayout>.Instance);

        var jobId = Guid.NewGuid();
        var first = layout.GetJobDir(jobId);
        var second = layout.GetJobDir(jobId);

        first.ShouldBe(second);
        first.ShouldEndWith(jobId.ToString());
    }

    private static IConfiguration BuildConfig(string workspaceBase, string modulesDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkspaceBase"] = workspaceBase,
                ["Provisioning:TerraformModulesDir"] = modulesDir,
                ["Provisioning:DefaultSize"] = "s-1vcpu-1gb",
            })
            .Build();

    private static (ProvisioningJob job, Cloud cloud) MakeJobAndCloud(string provider)
    {
        var cloud = new Cloud
        {
            UserId = Guid.NewGuid(),
            Name = "test",
            Provider = provider,
            Region = "nyc3",
            Hostname = "test.example.com",
            ProvisioningStatus = "pending",
        };
        var job = new ProvisioningJob
        {
            CloudId = cloud.Id,
            Kind = "provision",
            Payload = JsonDocument.Parse("{}"),
            Status = SagaStatus.TfPlanning,
            EventsLog = JsonDocument.Parse("[]"),
        };
        return (job, cloud);
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string root;
        public TempDir() { root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sw-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        public string Path(string sub) => System.IO.Path.Combine(root, sub);
        public void Dispose() { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
