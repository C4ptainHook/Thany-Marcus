using System.Text.RegularExpressions;
using Shouldly;
using ThanyMarcus.Portal.Tests.SagaWorker.Terraform;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Provisioning;

public sealed partial class CloudInitTemplateVariablesTests
{
    [Fact]
    public async Task CloudInitTemplate_DeclaresExactlyTheFrozenVariableSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var sharedDir = TerraformProcess.LocateModule(
            Path.Combine("infra", "docker", "saga-worker", "terraform-modules", "shared"));
        var templatePath = Path.Combine(sharedDir, "cloud-init.sh.tpl");
        var template = await File.ReadAllTextAsync(templatePath, ct);

        var expectedVars = new[] { "cloud_id", "hostname", "portal_url", "enrollment_token", "ghcr_pat" };

        // Terraform's templatefile() consumes single-$ interpolations. Literal $${name}
        // becomes a runtime shell substitution after templatefile() rewrites it. We only
        // want to inspect Terraform interpolations here, so reject any preceded by a $.
        var found = TerraformInterpolation()
            .Matches(template)
            .Where(m => m.Index == 0 || template[m.Index - 1] != '$')
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var v in expectedVars)
        {
            found.ShouldContain(v, $"template missing required variable ${{{v}}}; contract is frozen by PORTAL-008");
        }

        found.Count.ShouldBe(
            expectedVars.Length,
            $"Unexpected template var(s); contract is frozen by PORTAL-008. Found: {string.Join(", ", found)}");
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex TerraformInterpolation();
}
