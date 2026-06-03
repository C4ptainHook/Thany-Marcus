using System.Globalization;
using System.Text;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class ProcessingDetailsRenderer
{
    public static string Render(SynthesisFrontmatterFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var date = fields.SynthesizedAt.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture);
        var presetLabel = PresetLabel(fields.Preset, fields.PromptVersion);

        var sb = new StringBuilder();
        sb.AppendLine("> [!info]- Processing details");
        sb.Append("> Model: ").Append(fields.Model)
          .Append(" · ").Append(fields.PrivacyMode)
          .Append(" · ").Append(presetLabel)
          .Append(" · seed ").Append(fields.Seed.ToString(CultureInfo.InvariantCulture))
          .Append(" · synthesized ").Append(date)
          .Append(" · ").Append(fields.Status)
          .AppendLine();
        if (!string.IsNullOrWhiteSpace(fields.Error))
        {
            sb.Append("> error: ").AppendLine(OneLine(fields.Error!));
        }
        return sb.ToString();
    }

    private static string PresetLabel(string preset, string promptVersion)
    {
        var dash = promptVersion.LastIndexOf('-');
        var tail = dash >= 0 ? promptVersion[(dash + 1)..] : promptVersion;
        var isVersion = tail.Length > 1 && tail[0] == 'v' && tail[1..].All(char.IsDigit);
        return isVersion ? $"preset {preset} {tail}" : $"preset {preset} {promptVersion}";
    }

    private static string OneLine(string s)
    {
        var collapsed = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length > 240 ? collapsed[..240] + "…" : collapsed;
    }
}
