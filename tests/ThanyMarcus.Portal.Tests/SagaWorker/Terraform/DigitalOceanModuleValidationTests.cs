using Shouldly;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

public sealed class DigitalOceanModuleValidationTests
{
    [Fact]
    public async Task ModuleValidatesCleanly()
    {
        if (!TerraformProcess.IsAvailable())
        {
            Assert.Skip("terraform binary not in PATH; skipping HCL validation");
        }

        var ct = TestContext.Current.CancellationToken;
        var modulePath = TerraformProcess.LocateModule(
            Path.Combine("infra", "docker", "saga-worker", "terraform-modules", "digitalocean"));
        var workdir = Directory.CreateTempSubdirectory("do-tf-validate-").FullName;
        try
        {
            var moduleSource = modulePath.Replace("\\", "/", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(workdir, "main.tf"), $$"""
                terraform {
                  required_version = ">= 1.6"
                  required_providers {
                    digitalocean = {
                      source  = "digitalocean/digitalocean"
                      version = "~> 2.0"
                    }
                  }
                }

                module "do" {
                  source           = "{{moduleSource}}"
                  cloud_id         = "00000000-0000-0000-0000-000000000000"
                  region           = "fra1"
                  size             = "s-4vcpu-16gb"
                  hostname         = "test.thany.click"
                  enrollment_token = "fake"
                  ghcr_pat         = "fake"
                  portal_url       = "https://example.test/"
                  le_email         = "letsencrypt@example.test"
                  image_tag        = "test"
                }
                """, ct);

            var init = await TerraformProcess.RunAsync(
                ["init", "-backend=false", "-input=false", "-no-color"], workdir, ct: ct);
            init.ExitCode.ShouldBe(0, $"terraform init failed:\nstdout: {init.Stdout}\nstderr: {init.Stderr}");

            var validate = await TerraformProcess.RunAsync(
                ["validate", "-no-color"], workdir, ct: ct);
            validate.ExitCode.ShouldBe(0, $"terraform validate failed:\nstdout: {validate.Stdout}\nstderr: {validate.Stderr}");
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
