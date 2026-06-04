using System.Globalization;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public sealed partial class WorkspaceLayout(IConfiguration config, ILogger<WorkspaceLayout> log)
{
    private readonly string baseDir = config.GetValue("Provisioning:WorkspaceBase", "/var/lib/portal/terraform")!;
    private readonly string modulesDir = config.GetValue("Provisioning:TerraformModulesDir", "/app/terraform-modules")!;
    private readonly string defaultSize = config.GetValue("Provisioning:DefaultSize", "s-1vcpu-1gb")!;

    public string DefaultSize => defaultSize;

    public string GetJobDir(Guid jobId) => Path.Combine(baseDir, "jobs", jobId.ToString());

    public async Task<string> RenderAsync(ProvisioningJob job, Cloud cloud, CancellationToken ct)
    {
        var dir = GetJobDir(job.Id);
        Directory.CreateDirectory(dir);

        var tfLayout = ProviderTerraformCatalog.Get(cloud.Provider);
        var modulePath = Path.Combine(modulesDir, tfLayout.ModuleFolderName);
        if (!Directory.Exists(modulePath))
        {
            throw new InvalidOperationException($"Terraform module not found at {modulePath} (provider={cloud.Provider})");
        }

        var mainTf = RenderMainTf(modulePath, tfLayout);
        await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), mainTf, ct);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "backend.tf"),
            "terraform {\n  backend \"pg\" {}\n}\n",
            ct);

        string tfvars;
        if (!tfLayout.RequiresCloudInitTfvars)
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
            var cloudInit = ReadCloudInitConfig();
            var ollamaOverrides = RenderOllamaTfvarOverrides(cloudInit);
            tfvars = string.Create(CultureInfo.InvariantCulture, $"""
                cloud_id       = "{cloud.Id}"
                region         = "{cloud.Region}"
                size           = "{defaultSize}"
                hostname       = "{cloud.Hostname}"
                portal_url     = "{portalUrl}"
                le_email       = "{cloudInit.LeEmail}"
                le_acme_ca     = "{cloudInit.LeAcmeCa}"
                image_tag      = "{cloudInit.ImageTag}"
                ssh_public_key = "{cloudInit.SshPublicKey}"
                {ollamaOverrides}
                """);
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"), tfvars, ct);

        LogRendered(log, job.Id, cloud.Provider, dir);
        return dir;
    }

    private CloudInitConfig ReadCloudInitConfig()
    {
        var section = config.GetSection("Provisioning:CloudInit");
        string Require(string key) => section[key]
            ?? throw new InvalidOperationException($"Provisioning:CloudInit:{key} is not configured");
        return new CloudInitConfig(
            LeEmail:              Require("LeEmail"),
            LeAcmeCa:             section["LeAcmeCa"] ?? "",
            ImageTag:             Require("ImageTag"),
            SshPublicKey:         EscapeTfString(section["SshPublicKey"] ?? ""),
            OllamaVisionPullTag:  section["OllamaVisionPullTag"] ?? "",
            OllamaTextPullTag:    section["OllamaTextPullTag"] ?? "",
            OllamaTextImageTag:   section["OllamaTextImageTag"] ?? "");
    }

    private static string RenderOllamaTfvarOverrides(CloudInitConfig cloudInit)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(cloudInit.OllamaVisionPullTag))
            sb.AppendLine(CultureInfo.InvariantCulture, $"ollama_vision_pull_tag = \"{cloudInit.OllamaVisionPullTag}\"");
        if (!string.IsNullOrWhiteSpace(cloudInit.OllamaTextPullTag))
            sb.AppendLine(CultureInfo.InvariantCulture, $"ollama_text_pull_tag   = \"{cloudInit.OllamaTextPullTag}\"");
        if (!string.IsNullOrWhiteSpace(cloudInit.OllamaTextImageTag))
            sb.AppendLine(CultureInfo.InvariantCulture, $"ollama_text_image_tag  = \"{cloudInit.OllamaTextImageTag}\"");
        return sb.ToString().TrimEnd();
    }

    private static string EscapeTfString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record CloudInitConfig(
        string LeEmail,
        string LeAcmeCa,
        string ImageTag,
        string SshPublicKey,
        string OllamaVisionPullTag,
        string OllamaTextPullTag,
        string OllamaTextImageTag);

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

    private static string RenderMainTf(string modulePath, ProviderTerraformLayout tfLayout)
    {
        var requiredProviders = tfLayout.RequiredProvidersBlock;
        var providerBlock     = tfLayout.ProviderBlock;
        var extraModuleArgs   = tfLayout.ExtraModuleArgs;
        var extraRootVars     = tfLayout.ExtraRootVars;

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
