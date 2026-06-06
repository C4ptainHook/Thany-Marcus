using System.Text.Json;
using Shouldly;
using YamlDotNet.Serialization;

namespace ThanyMarcus.Portal.Tests.CloudInit;

public sealed class CloudInitUpdaterTests
{
    private static readonly Lazy<Dictionary<object, object>> Parsed = new(Parse);
    private static readonly Lazy<string> RenderedTemplate = new(Render);

    [Fact]
    public void Ships_systemd_path_and_service_units_for_the_in_place_updater()
    {
        var pathUnit = WriteFile("/etc/systemd/system/thany-update.path");
        pathUnit.ShouldContain("PathExists=/mnt/thany-data/cloud-state/pending-update.json");
        pathUnit.ShouldContain("Unit=thany-update.service");

        var serviceUnit = WriteFile("/etc/systemd/system/thany-update.service");
        serviceUnit.ShouldContain("Type=oneshot");
        serviceUnit.ShouldContain("ExecStart=/opt/thany-cloud/update/apply.sh");
    }

    [Fact]
    public void Updater_enforces_namespace_digest_pin_monotonic_and_blue_green_routing()
    {
        var script = WriteFile("/opt/thany-cloud/update/apply.sh");
        script.ShouldContain("c4ptainhook/");
        script.ShouldContain("@sha256:");
        script.ShouldContain("^sha256:[0-9a-f]{64}$");
        script.ShouldContain("ver_gt");
        script.ShouldContain("requires_blue_green");
        script.ShouldContain("schema_min_from");
    }

    [Fact]
    public void Updater_merges_env_overlay_but_never_rewrites_secrets()
    {
        var script = WriteFile("/opt/thany-cloud/update/apply.sh");
        script.ShouldContain("CLOUD_ADMIN_TOKEN|JWT_SIGNING_KEY|POSTGRES_PASSWORD");
        script.ShouldContain("env_overlay // {}");
    }

    [Fact]
    public void Updater_health_gates_then_commits_or_rolls_back()
    {
        var script = WriteFile("/opt/thany-cloud/update/apply.sh");
        script.ShouldContain("/health/ready");
        script.ShouldContain("/admin/health");
        script.ShouldContain("rollback");
        script.ShouldContain("write_status committed");
    }

    [Fact]
    public void Cloud_state_dir_is_seeded_and_bind_mounted_into_cloud_api()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("mkdir -p /mnt/thany-data/cloud-state");
        runcmd.ShouldContain("/mnt/thany-data/cloud-state/current_version");
        runcmd.ShouldContain("systemctl enable --now thany-update.path");

        var compose = WriteFile("/opt/thany-cloud/docker-compose.yml");
        compose.ShouldContain("/mnt/thany-data/cloud-state:/mnt/thany-data/cloud-state");
    }

    private static string WriteFile(string path)
    {
        var writeFiles = (IEnumerable<object>)Parsed.Value["write_files"];
        foreach (var entryObj in writeFiles)
        {
            var entry = (IDictionary<object, object>)entryObj;
            if (entry.TryGetValue("path", out var p) && p?.ToString() == path)
                return entry["content"]?.ToString() ?? "";
        }
        throw new KeyNotFoundException($"No write_files entry for path '{path}'.");
    }

    private static string RuncmdText() =>
        string.Join('\n', ((IEnumerable<object>)Parsed.Value["runcmd"]).Select(c => c?.ToString() ?? ""));

    private static Dictionary<object, object> Parse() =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(RenderedTemplate.Value);

    private static string Render()
    {
        var path = Path.Combine(
            TerraformTemplateRenderer.FindRepoRoot(),
            "infra", "docker", "saga-worker", "terraform-modules", "digitalocean", "cloud-init.yaml.tpl");
        return TerraformTemplateRenderer.Render(File.ReadAllText(path), LoadSampleVars());
    }

    private static Dictionary<string, string> LoadSampleVars()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CloudInit", "Fixtures", "sample-vars.tfvars.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(
                TerraformTemplateRenderer.FindRepoRoot(),
                "tests", "ThanyMarcus.Portal.Tests", "CloudInit", "Fixtures", "sample-vars.tfvars.json");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dict = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
        }
        return dict;
    }
}
