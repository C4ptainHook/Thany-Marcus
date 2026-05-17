using Shouldly;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

[Trait("Category", "ManualDigitalOcean")]
public sealed class DigitalOceanModulePlanTests
{
    [Fact]
    public async Task PlanReachesDOApi_FailsWith401_OnFakeToken()
    {
        if (!TerraformProcess.IsAvailable())
        {
            Assert.Skip("terraform binary not in PATH");
        }

        var ct = TestContext.Current.CancellationToken;
        var modulePath = TerraformProcess.LocateModule(
            Path.Combine("infra", "docker", "saga-worker", "terraform-modules", "digitalocean"));
        var workdir = Directory.CreateTempSubdirectory("do-tf-plan-").FullName;
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
            init.ExitCode.ShouldBe(0, init.Stderr);

            var plan = await TerraformProcess.RunAsync(
                ["plan", "-no-color", "-input=false"], workdir,
                env: new Dictionary<string, string> { ["TF_VAR_provider_token"] = "dop_v1_fake_xxxxxxxxxx" },
                ct: ct);

            plan.ExitCode.ShouldNotBe(0, "plan should fail — fake token");
            var combined = plan.Stdout + plan.Stderr;
            (combined.Contains("401", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue($"expected 401/Unauthorized in terraform output. Got:\n{combined}");
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
