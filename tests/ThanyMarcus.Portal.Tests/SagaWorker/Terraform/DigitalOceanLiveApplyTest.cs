using Shouldly;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

[Trait("Category", "LiveDigitalOcean")]
public sealed class DigitalOceanLiveApplyTest
{
    [Fact]
    public async Task FullProvisionAndDestroyCycle()
    {
        var liveToken = Environment.GetEnvironmentVariable("DO_TF_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(liveToken))
        {
            Assert.Skip("DO_TF_LIVE_TOKEN not set; skipping live test (cost ~$0.10)");
        }
        if (!TerraformProcess.IsAvailable())
        {
            Assert.Skip("terraform binary not in PATH");
        }

        var ct = TestContext.Current.CancellationToken;
        var modulePath = TerraformProcess.LocateModule(
            Path.Combine("infra", "docker", "saga-worker", "terraform-modules", "digitalocean"));
        var workdir = Directory.CreateTempSubdirectory("do-tf-live-").FullName;
        var applied = false;
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

                provider "digitalocean" {
                  token = var.provider_token
                }

                variable "provider_token" {
                  type      = string
                  sensitive = true
                }

                module "do" {
                  source           = "{{moduleSource}}"
                  cloud_id         = "00000000-0000-0000-0000-000000000000"
                  region           = "fra1"
                  size             = "s-4vcpu-16gb"
                  hostname         = "live-test.thany.click"
                  enrollment_token = "live-fake"
                  ghcr_pat         = "live-fake"
                  portal_url       = "https://example.test/"
                }

                output "ip" { value = module.do.ip }
                """, ct);

            var env = new Dictionary<string, string> { ["TF_VAR_provider_token"] = liveToken! };
            var init = await TerraformProcess.RunAsync(
                ["init", "-backend=false", "-input=false", "-no-color"], workdir, env: env, ct: ct);
            init.ExitCode.ShouldBe(0, init.Stderr);

            var apply = await TerraformProcess.RunAsync(
                ["apply", "-auto-approve", "-no-color", "-input=false"], workdir, env: env, ct: ct);
            applied = true;
            apply.ExitCode.ShouldBe(0, apply.Stderr);

            var output = await TerraformProcess.RunAsync(
                ["output", "-raw", "ip"], workdir, env: env, ct: ct);
            output.ExitCode.ShouldBe(0);
            output.Stdout.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            if (applied)
            {
                var env = new Dictionary<string, string> { ["TF_VAR_provider_token"] = liveToken! };
                await TerraformProcess.RunAsync(
                    ["destroy", "-auto-approve", "-no-color", "-input=false"], workdir, env: env, ct: ct);
            }
            try { Directory.Delete(workdir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
