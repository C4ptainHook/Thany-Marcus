using System.Text;
using System.Text.RegularExpressions;

namespace ThanyMarcus.Portal.Tests.CloudInit;

internal static class TerraformTemplateRenderer
{
    private static readonly Regex InterpolationOrEscape = new(
        @"\$\$|\$\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder(template.Length);
        var i = 0;
        foreach (Match m in InterpolationOrEscape.Matches(template))
        {
            sb.Append(template, i, m.Index - i);
            if (m.Value == "$$")
            {
                sb.Append('$');
            }
            else
            {
                var name = m.Groups["name"].Value;
                if (!vars.TryGetValue(name, out var value))
                {
                    throw new KeyNotFoundException(
                        $"Template references variable '{name}' but no value was supplied.");
                }
                sb.Append(value);
            }
            i = m.Index + m.Length;
        }
        sb.Append(template, i, template.Length - i);
        return sb.ToString();
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (no global.json ancestor).");
        }
        return dir.FullName;
    }
}
