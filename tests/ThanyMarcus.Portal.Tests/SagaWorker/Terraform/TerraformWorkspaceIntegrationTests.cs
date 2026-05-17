using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

[Trait("requires", "terraform")]
public sealed class TerraformWorkspaceIntegrationTests
{
    [Fact]
    public async Task Rendered_workspace_passes_terraform_validate_for_digitalocean()
    {
        if (!TerraformProcess.IsAvailable())
        {
            Assert.Skip("terraform binary not in PATH");
        }

        var ct = TestContext.Current.CancellationToken;
        var modulesRoot = TerraformProcess.LocateModule(
            Path.Combine("infra", "docker", "saga-worker", "terraform-modules"));
        var workspaceBase = Directory.CreateTempSubdirectory("tf-ws-validate-").FullName;
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Provisioning:WorkspaceBase"] = workspaceBase,
                    ["Provisioning:TerraformModulesDir"] = modulesRoot,
                    ["Provisioning:DefaultSize"] = "s-2vcpu-4gb",
                    ["Provisioning:PortalUrl"] = "https://api.test/",
                    ["Provisioning:CloudInit:LeEmail"] = "letsencrypt@example.test",
                    ["Provisioning:CloudInit:ImageTag"] = "test",
                })
                .Build();

            var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);
            var (job, cloud) = MakeJobAndCloud("digitalocean");

            var workdir = await layout.RenderAsync(job, cloud, ct);

            // Drop backend.tf so `init -backend=false` finds nothing to configure.
            File.Delete(Path.Combine(workdir, "backend.tf"));

            var init = await TerraformProcess.RunAsync(
                ["init", "-backend=false", "-input=false", "-no-color"], workdir, ct: ct);
            init.ExitCode.ShouldBe(0, $"terraform init failed:\nstdout: {init.Stdout}\nstderr: {init.Stderr}");

            var validate = await TerraformProcess.RunAsync(
                ["validate", "-no-color"], workdir, ct: ct);
            validate.ExitCode.ShouldBe(0,
                $"terraform validate failed:\nstdout: {validate.Stdout}\nstderr: {validate.Stderr}");
        }
        finally
        {
            try { Directory.Delete(workspaceBase, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static (ProvisioningJob job, Cloud cloud) MakeJobAndCloud(string provider)
    {
        var cloud = new Cloud
        {
            UserId = Guid.NewGuid(),
            Name = "test",
            Provider = provider,
            Region = "nyc3",
            Hostname = "test.thany.click",
            ProvisioningStatus = SagaStatus.TfPlanning,
        };
        var job = new ProvisioningJob
        {
            CloudId = cloud.Id,
            UserId = cloud.UserId,
            Kind = "provision",
            Payload = JsonDocument.Parse("{}"),
            Status = SagaStatus.TfPlanning,
            EventsLog = JsonDocument.Parse("[]"),
        };
        return (job, cloud);
    }
}
