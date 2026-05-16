using System.Globalization;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public sealed partial class WorkspaceLayout(IConfiguration config, ILogger<WorkspaceLayout> log)
{
    private readonly string baseDir = config.GetValue("Provisioning:WorkspaceBase", "/var/lib/portal/terraform")!;
    private readonly string modulesDir = config.GetValue("Provisioning:TerraformModulesDir", "/app/terraform-modules")!;
    private readonly string defaultSize = config.GetValue("Provisioning:DefaultSize", "s-1vcpu-1gb")!;

    public string GetJobDir(Guid jobId) => Path.Combine(baseDir, "jobs", jobId.ToString());

    public async Task<string> RenderAsync(ProvisioningJob job, Cloud cloud, CancellationToken ct)
    {
        var dir = GetJobDir(job.Id);
        Directory.CreateDirectory(dir);

        var modulePath = Path.Combine(modulesDir, cloud.Provider);
        if (!Directory.Exists(modulePath))
        {
            throw new InvalidOperationException($"Terraform module not found at {modulePath} (provider={cloud.Provider})");
        }

        var mainTf = RenderMainTf(modulePath, cloud.Provider);
        await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), mainTf, ct);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "backend.tf"),
            "terraform {\n  backend \"pg\" {}\n}\n",
            ct);

        string tfvars;
        if (cloud.Provider == "stub")
        {
            tfvars = string.Create(CultureInfo.InvariantCulture, $"""
                cloud_id = "{cloud.Id}"
                region   = "{cloud.Region}"
                size     = "{defaultSize}"
                hostname = "{cloud.Hostname}"
                """);
        }
        else
        {
            var portalUrl = config["Provisioning:PortalUrl"]
                ?? throw new InvalidOperationException("Provisioning:PortalUrl is not configured");
            tfvars = string.Create(CultureInfo.InvariantCulture, $"""
                cloud_id   = "{cloud.Id}"
                region     = "{cloud.Region}"
                size       = "{defaultSize}"
                hostname   = "{cloud.Hostname}"
                portal_url = "{portalUrl}"
                """);
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"), tfvars, ct);

        LogRendered(log, job.Id, cloud.Provider, dir);
        return dir;
    }

    public void SweepTerminal(Instant now, Duration retention)
    {
        var jobsDir = Path.Combine(baseDir, "jobs");
        if (!Directory.Exists(jobsDir))
        {
            return;
        }

        var cutoff = (now - retention).ToDateTimeUtc();
        foreach (var dir in Directory.EnumerateDirectories(jobsDir))
        {
            var info = new DirectoryInfo(dir);
            if (info.LastWriteTimeUtc < cutoff)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    LogSwept(log, dir);
                }
                catch (IOException ex)
                {
                    LogSweepFailed(log, ex, dir);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogSweepFailed(log, ex, dir);
                }
            }
        }
    }

    private static string RenderMainTf(string modulePath, string provider)
    {
        var (requiredProviders, providerBlock, extraModuleArgs, extraRootVars) = provider switch
        {
            "stub" => (
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty),
            "digitalocean" => (
                """
                  required_providers {
                    digitalocean = {
                      source  = "digitalocean/digitalocean"
                      version = "~> 2.0"
                    }
                  }
                """,
                """

                provider "digitalocean" {
                  token = var.provider_token
                }
                """,
                """

                  enrollment_token = var.enrollment_token
                  ghcr_pat         = var.ghcr_pat
                  portal_url       = var.portal_url
                """,
                """

                variable "provider_token" {
                  type      = string
                  sensitive = true
                  default   = ""
                }
                variable "enrollment_token" {
                  type      = string
                  sensitive = true
                  default   = ""
                }
                variable "ghcr_pat" {
                  type      = string
                  sensitive = true
                  default   = ""
                }
                variable "portal_url" {
                  type = string
                }
                """),
            "azure" => throw new NotImplementedException("Azure provider lands in PORTAL-009"),
            _ => throw new InvalidOperationException($"Unknown provider: {provider}"),
        };

        return $$"""
            terraform {
              required_version = ">= 1.6"
            {{requiredProviders}}
            }
            {{providerBlock}}
            variable "cloud_id" { type = string }
            variable "region"   { type = string }
            variable "size"     { type = string }
            variable "hostname" { type = string }
            {{extraRootVars}}
            module "cloud" {
              source   = "{{modulePath.Replace("\\", "/", StringComparison.Ordinal)}}"
              cloud_id = var.cloud_id
              region   = var.region
              size     = var.size
              hostname = var.hostname
            {{extraModuleArgs}}
            }

            output "ip" { value = module.cloud.ip }
            """;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Rendered workspace for job {JobId} provider={Provider} dir={Dir}")]
    private static partial void LogRendered(ILogger logger, Guid jobId, string provider, string dir);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Swept terminal workspace {Dir}")]
    private static partial void LogSwept(ILogger logger, string dir);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Failed to sweep terminal workspace {Dir} (will retry next boot)")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex, string dir);
}
