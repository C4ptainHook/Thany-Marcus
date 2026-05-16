using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using YamlDotNet.Serialization;

namespace ThanyMarcus.Portal.Tests.CloudInit;

public sealed class CloudInitTemplateRenderTests
{
    private static readonly Lazy<string> Rendered = new(RenderTemplate);
    private static readonly Lazy<Dictionary<object, object>> Parsed = new(ParseRendered);
    private static readonly Lazy<Dictionary<string, string>> SampleVars = new(LoadSampleVars);

    [Fact]
    public void Template_file_exists_at_canonical_path()
    {
        var path = Path.Combine(
            TerraformTemplateRenderer.FindRepoRoot(),
            "infra", "docker", "saga-worker", "terraform-modules", "digitalocean", "cloud-init.yaml.tpl");
        File.Exists(path).ShouldBeTrue($"missing: {path}");
    }

    [Fact]
    public void Renders_to_valid_cloud_config_yaml()
    {
        var rendered = Rendered.Value;
        rendered.ShouldStartWith("#cloud-config");
        Parsed.Value.ShouldNotBeNull();
    }

    [Fact]
    public void Top_level_keys_match_cloud_init_v23_contract()
    {
        var doc = Parsed.Value;
        foreach (var key in new[] { "bootcmd", "users", "packages", "write_files", "runcmd", "final_message" })
        {
            doc.ShouldContainKey(key);
        }
    }

    [Fact]
    public void No_unsubstituted_terraform_variables_remain()
    {
        var rendered = Rendered.Value;
        var terraformVars = string.Join("|", SampleVars.Value.Keys.Select(Regex.Escape));
        var pattern = $@"(?<!\$)\$\{{\s*({terraformVars})\s*\}}";
        var leaks = Regex.Matches(rendered, pattern)
            .Select(m => m.Value)
            .ToList();
        leaks.ShouldBeEmpty();
    }

    [Fact]
    public void Preserves_runtime_envsubst_placeholders_for_caddyfile()
    {
        var rendered = Rendered.Value;
        rendered.ShouldContain("${DOMAIN}");
        rendered.ShouldContain("${LE_EMAIL}");
    }

    [Fact]
    public void Substitutes_all_terraform_variables_into_output()
    {
        var rendered = Rendered.Value;
        var vars = SampleVars.Value;
        foreach (var key in new[] { "cloud_id", "hostname", "enrollment_token",
                                    "portal_callback_url", "le_email", "image_tag",
                                    "admin_user", "ssh_public_key", "compose_url", "timezone" })
        {
            rendered.ShouldContain(vars[key], Case.Sensitive, $"variable '{key}' was not interpolated");
        }
    }

    [Fact]
    public void Hardens_sshd_with_password_auth_disabled_and_root_login_off()
    {
        var hardening = ExtractWriteFile("/etc/ssh/sshd_config.d/01-hardening.conf");
        hardening.ShouldContain("PasswordAuthentication no");
        hardening.ShouldContain("PermitRootLogin no");
        hardening.ShouldContain($"AllowUsers {SampleVars.Value["admin_user"]}");
    }

    [Fact]
    public void Disables_ssh_password_auth_at_cloud_init_level()
    {
        Rendered.Value.ShouldMatch(@"ssh_pwauth:\s*false");
    }

    [Fact]
    public void Installs_envsubst_jq_curl_and_hardening_packages()
    {
        var packages = ((IEnumerable<object>)Parsed.Value["packages"])
            .Select(p => p.ToString()!)
            .ToHashSet();
        foreach (var required in new[] { "ufw", "fail2ban", "unattended-upgrades",
                                         "curl", "jq", "gettext-base", "ca-certificates", "gnupg" })
        {
            packages.ShouldContain(required);
        }
    }

    [Fact]
    public void Configures_ufw_only_for_ssh_and_http_s()
    {
        var ufwApp = ExtractWriteFile("/etc/ufw/applications.d/thany-cloud");
        ufwApp.ShouldContain("80,443/tcp");

        var runcmd = RuncmdLines();
        runcmd.ShouldContain(l => l.Contains("ufw default deny incoming"));
        runcmd.ShouldContain(l => l.Contains("ufw limit OpenSSH"));
        runcmd.ShouldContain(l => l.Contains("ufw allow thany-cloud-web"));
        runcmd.ShouldContain(l => l.Contains("ufw --force enable"));
        runcmd.ShouldNotContain(l => Regex.IsMatch(l, @"ufw\s+allow\s+8080"));
    }

    [Fact]
    public void Installs_docker_via_apt_source_not_legacy_compose()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("download.docker.com");
        runcmd.ShouldContain("docker-ce");
        runcmd.ShouldContain("docker-compose-plugin");
        runcmd.ShouldNotContain("pip install docker-compose");
        runcmd.ShouldNotContain("apt-get install -y docker-compose ");
    }

    [Fact]
    public void Generates_secrets_via_openssl_into_tmpfs()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("openssl rand -hex 32");
        runcmd.ShouldContain("/run/cloud-secrets/cloud_admin_token");
        runcmd.ShouldContain("/run/cloud-secrets/jwt_signing_key");
    }

    [Fact]
    public void Mkdir_for_tmpfs_secrets_runs_in_bootcmd_before_runcmd()
    {
        var bootcmd = ((IEnumerable<object>)Parsed.Value["bootcmd"])
            .Select(c => c.ToString()!)
            .ToList();
        bootcmd.ShouldContain(l => l.Contains("/run/cloud-secrets"));
        bootcmd.ShouldContain(l => l.Contains("chmod 0700 /run/cloud-secrets"));
    }

    [Fact]
    public void Env_template_carries_runtime_secret_placeholders()
    {
        var envTpl = ExtractWriteFile("/etc/thany-cloud/cloud.env.tpl");
        envTpl.ShouldContain("CLOUD_ADMIN_TOKEN=${CLOUD_ADMIN_TOKEN}");
        envTpl.ShouldContain("JWT_SIGNING_KEY=${JWT_SIGNING_KEY}");
        envTpl.ShouldContain($"CLOUD_ID={SampleVars.Value["cloud_id"]}");
        envTpl.ShouldContain($"DOMAIN={SampleVars.Value["hostname"]}");
        envTpl.ShouldContain($"ENROLLMENT_TOKEN={SampleVars.Value["enrollment_token"]}");
    }

    [Fact]
    public void Registration_script_polls_admin_health_then_posts_callback()
    {
        var script = ExtractWriteFile("/usr/local/bin/register-with-portal.sh");
        script.ShouldStartWith("#!/usr/bin/env bash");
        script.ShouldContain("set -euo pipefail");
        script.ShouldContain("/admin/health");
        script.ShouldContain("cert_ready");
        script.ShouldContain("PORTAL_CALLBACK_URL");
        script.ShouldContain("enrollment_token");
        script.ShouldContain("cloud_admin_token");
        script.ShouldContain("cloud_id");
    }

    [Fact]
    public void Registration_script_bounds_retries_and_backoff()
    {
        var script = ExtractWriteFile("/usr/local/bin/register-with-portal.sh");
        script.ShouldMatch(@"ATTEMPT\s*<\s*8");
        script.ShouldContain("2 ** ATTEMPT");
    }

    [Fact]
    public void Systemd_units_for_compose_and_registration_are_present()
    {
        var compose = ExtractWriteFile("/etc/systemd/system/thany-cloud.service");
        compose.ShouldContain("ExecStart=/usr/bin/docker compose up -d");
        compose.ShouldContain($"User={SampleVars.Value["admin_user"]}");

        var register = ExtractWriteFile("/etc/systemd/system/thany-cloud-register.service");
        register.ShouldContain("ExecStart=/usr/local/bin/register-with-portal.sh");
        register.ShouldContain("Requires=thany-cloud.service");
    }

    [Fact]
    public void Caddyfile_template_pins_le_email_and_reverse_proxies_admin_and_api()
    {
        var caddy = ExtractWriteFile("/etc/thany-cloud/Caddyfile.tpl");
        caddy.ShouldContain("email ${LE_EMAIL}");
        caddy.ShouldContain("${DOMAIN}");
        caddy.ShouldContain("reverse_proxy /admin/* cloud-api:8080");
        caddy.ShouldContain("reverse_proxy /api/*");
    }

    [Fact]
    public void Compose_file_fetched_from_pinned_url()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain(SampleVars.Value["compose_url"]);
        runcmd.ShouldContain("/opt/thany-cloud/docker-compose.yml");
    }

    [Fact]
    public void Final_message_is_set()
    {
        Parsed.Value["final_message"].ToString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Le_acme_ca_override_is_threaded_through_when_set()
    {
        var vars = new Dictionary<string, string>(SampleVars.Value)
        {
            ["le_acme_ca"] = "https://acme-staging-v02.api.letsencrypt.org/directory",
        };
        var rendered = RenderWith(vars);
        rendered.ShouldContain("acme-staging-v02");
    }

    [Fact]
    public void Le_acme_ca_empty_keeps_prod_endpoint()
    {
        var rendered = Rendered.Value;
        rendered.ShouldNotContain("acme-staging-v02");
    }

    private static string ExtractWriteFile(string path)
    {
        var writeFiles = (IEnumerable<object>)Parsed.Value["write_files"];
        foreach (var entryObj in writeFiles)
        {
            var entry = (IDictionary<object, object>)entryObj;
            if (entry.TryGetValue("path", out var p) && p?.ToString() == path)
            {
                return entry["content"]?.ToString() ?? "";
            }
        }
        throw new KeyNotFoundException($"No write_files entry for path '{path}'.");
    }

    private static List<string> RuncmdLines() =>
        ((IEnumerable<object>)Parsed.Value["runcmd"])
            .Select(c => c?.ToString() ?? "")
            .ToList();

    private static string RuncmdText() => string.Join('\n', RuncmdLines());

    private static string RenderTemplate() => RenderWith(SampleVars.Value);

    private static string RenderWith(IReadOnlyDictionary<string, string> vars)
    {
        var path = Path.Combine(
            TerraformTemplateRenderer.FindRepoRoot(),
            "infra", "docker", "saga-worker", "terraform-modules", "digitalocean", "cloud-init.yaml.tpl");
        var template = File.ReadAllText(path);
        return TerraformTemplateRenderer.Render(template, vars);
    }

    private static Dictionary<object, object> ParseRendered()
    {
        var deserializer = new DeserializerBuilder().Build();
        return deserializer.Deserialize<Dictionary<object, object>>(Rendered.Value);
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
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                _ => prop.Value.ToString(),
            };
        }
        return dict;
    }
}
