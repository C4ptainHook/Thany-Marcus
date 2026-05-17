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
    public void Preserves_runtime_envsubst_placeholders()
    {
        var rendered = Rendered.Value;
        rendered.ShouldContain("${DOMAIN}");
    }

    [Fact]
    public void Substitutes_all_terraform_variables_into_output()
    {
        var rendered = Rendered.Value;
        var vars = SampleVars.Value;
        foreach (var key in new[] { "cloud_id", "hostname", "enrollment_token",
                                    "portal_callback_url", "le_email", "image_tag",
                                    "admin_user", "ssh_public_key", "timezone" })
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
    public void Installs_required_packages_including_nginx_and_certbot()
    {
        var packages = ((IEnumerable<object>)Parsed.Value["packages"])
            .Select(p => p.ToString()!)
            .ToHashSet();
        foreach (var required in new[] { "ufw", "fail2ban", "unattended-upgrades",
                                         "curl", "jq", "gettext-base", "ca-certificates", "gnupg",
                                         "nginx", "certbot", "python3-certbot-nginx" })
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
        envTpl.ShouldContain("POSTGRES_PASSWORD=${POSTGRES_PASSWORD}");
        envTpl.ShouldContain($"CLOUD_ID={SampleVars.Value["cloud_id"]}");
        envTpl.ShouldContain($"DOMAIN={SampleVars.Value["hostname"]}");
        envTpl.ShouldContain($"ENROLLMENT_TOKEN={SampleVars.Value["enrollment_token"]}");
    }

    [Fact]
    public void Registration_lives_in_cloud_api_not_in_a_shell_script()
    {
        var rendered = Rendered.Value;
        rendered.ShouldNotContain("/usr/local/bin/register-with-portal.sh");
        rendered.ShouldNotContain("thany-cloud-register.service");
    }

    [Fact]
    public void Systemd_unit_for_compose_is_present()
    {
        var compose = ExtractWriteFile("/etc/systemd/system/thany-cloud.service");
        compose.ShouldContain("ExecStart=/usr/bin/docker compose up -d");
        compose.ShouldContain($"User={SampleVars.Value["admin_user"]}");
    }

    [Fact]
    public void Nginx_site_template_proxies_to_loopback_and_blocks_internal_path()
    {
        var site = ExtractWriteFile("/etc/thany-cloud/nginx-site.tpl");
        site.ShouldContain("listen 80;");
        site.ShouldContain("server_name ${DOMAIN};");
        site.ShouldContain("proxy_pass http://127.0.0.1:8080;");
        site.ShouldContain("location /internal/");
        site.ShouldContain("return 404;");
        site.ShouldContain("proxy_set_header Host $host;");
        site.ShouldContain("proxy_set_header X-Forwarded-Proto $scheme;");
        site.ShouldNotContain("listen 443");
    }

    [Fact]
    public void Cloud_init_does_not_reference_caddy_anywhere()
    {
        var rendered = Rendered.Value;
        rendered.ShouldNotContain("caddy", Case.Insensitive);
        rendered.ShouldNotContain("Caddyfile");
        rendered.ShouldNotContain("events.handlers.exec");
        rendered.ShouldNotContain("cert_obtained");
        rendered.ShouldNotContain("/internal/caddy-events");
    }

    [Fact]
    public void Compose_file_runs_cloud_api_with_loopback_port_and_letsencrypt_mount()
    {
        var compose = ExtractWriteFile("/opt/thany-cloud/docker-compose.yml");
        compose.ShouldContain("services:");
        compose.ShouldContain("cloud-api:");
        compose.ShouldContain("postgres:");
        compose.ShouldContain("ghcr.io/c4ptainhook/thany-cloud-api");
        compose.ShouldContain("Bootstrap__CloudId");
        compose.ShouldContain("Bootstrap__PortalCallbackUrl");
        compose.ShouldContain("Cert__LiveDir: /etc/letsencrypt/live/${DOMAIN}");
        compose.ShouldContain("\"127.0.0.1:8080:8080\"");
        compose.ShouldContain("/etc/letsencrypt:/etc/letsencrypt:ro");
        compose.ShouldNotContain("caddy:");
        compose.ShouldNotContain("Caddy__AdminUrl");
        compose.ShouldNotContain("caddy-data");
        compose.ShouldNotContain("caddy-config");
        compose.ShouldNotContain("cert-watcher:");
        compose.ShouldNotContain("nginx:alpine");
    }

    [Fact]
    public void Compose_does_not_publish_cloud_api_to_public_interface()
    {
        var compose = ExtractWriteFile("/opt/thany-cloud/docker-compose.yml");
        compose.ShouldNotMatch(@"^\s*-\s*""8080:8080""");
        compose.ShouldNotMatch(@"^\s*-\s*""0\.0\.0\.0:8080:8080""");
    }

    [Fact]
    public void Runcmd_renders_nginx_site_and_restarts_nginx()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("envsubst '$DOMAIN' < /etc/thany-cloud/nginx-site.tpl");
        runcmd.ShouldContain("/etc/nginx/sites-available/$DOMAIN");
        runcmd.ShouldContain("/etc/nginx/sites-enabled/$DOMAIN");
        runcmd.ShouldContain("rm -f /etc/nginx/sites-enabled/default");
        runcmd.ShouldContain("nginx -t");
        runcmd.ShouldContain("systemctl restart nginx");
    }

    [Fact]
    public void Runcmd_invokes_certbot_with_deploy_hook_targeting_cert_installed()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("certbot --nginx");
        runcmd.ShouldContain("--deploy-hook");
        runcmd.ShouldContain("--non-interactive");
        runcmd.ShouldContain("--agree-tos");
        runcmd.ShouldContain("--redirect");
        runcmd.ShouldContain("/internal/cert-installed");
        runcmd.ShouldContain("http://127.0.0.1:8080/internal/cert-installed");
        runcmd.ShouldContain("http://127.0.0.1:8080/health/live");
    }

    [Fact]
    public void Runcmd_orders_docker_stack_before_certbot_run()
    {
        var runcmdText = RuncmdText();
        var systemdIdx = runcmdText.IndexOf("systemctl enable --now thany-cloud.service", StringComparison.Ordinal);
        var certbotIdx = runcmdText.IndexOf("certbot --nginx", StringComparison.Ordinal);
        systemdIdx.ShouldBeGreaterThanOrEqualTo(0);
        certbotIdx.ShouldBeGreaterThan(systemdIdx);
    }

    [Fact]
    public void Runcmd_does_not_curl_compose_or_nginx_assets()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldNotContain("compose_url");
        runcmd.ShouldNotContain("nginx_conf_url");
    }

    [Fact]
    public void Runcmd_generates_postgres_password_and_does_not_enable_register_unit()
    {
        var runcmd = RuncmdText();
        runcmd.ShouldContain("/run/cloud-secrets/postgres_password");
        runcmd.ShouldContain("POSTGRES_PASSWORD=$(cat /run/cloud-secrets/postgres_password)");
        runcmd.ShouldNotContain("thany-cloud-register");
    }

    [Fact]
    public void Final_message_is_set()
    {
        Parsed.Value["final_message"].ToString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Le_acme_ca_staging_url_threads_staging_flag_into_certbot()
    {
        var vars = new Dictionary<string, string>(SampleVars.Value)
        {
            ["le_acme_ca"] = "https://acme-staging-v02.api.letsencrypt.org/directory",
        };
        var rendered = RenderWith(vars);
        rendered.ShouldContain("https://acme-staging-v02.api.letsencrypt.org/directory");
        rendered.ShouldContain("CERTBOT_FLAGS=\"--staging\"");
    }

    [Fact]
    public void Le_acme_ca_empty_does_not_add_staging_flag_unconditionally()
    {
        var rendered = Rendered.Value;
        rendered.ShouldNotMatch(@"CERTBOT_FLAGS=""--staging""\s*$");
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
