using System.Text.RegularExpressions;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

internal static partial class RouteV1Salvage
{
    [GeneratedRegex(
        "\"project_entity_id\"\\s*:\\s*(?:\"(?<id>[0-9a-fA-F\\-]{36})\"|null)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex(
        "\"confidence\"\\s*:\\s*(?<conf>-?\\d+(?:\\.\\d+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(
        "\"rationale\"\\s*:\\s*\"(?<r>(?:[^\"\\\\]|\\\\.)*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RationaleRegex();

    public static bool TryRecover<T>(PromptId promptId, string raw, out T? recovered)
        where T : class
    {
        recovered = null;
        if (typeof(T) != typeof(RouteDecisionDto)) return false;
        if (promptId.Name != "route" || promptId.Version != "v1") return false;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var idMatch = ProjectIdRegex().Match(raw);
        var confMatch = ConfidenceRegex().Match(raw);
        if (!confMatch.Success) return false;

        if (!double.TryParse(confMatch.Groups["conf"].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var confidence))
        {
            return false;
        }

        Guid? projectId = null;
        if (idMatch.Success && idMatch.Groups["id"].Success &&
            Guid.TryParse(idMatch.Groups["id"].Value, out var parsed))
        {
            projectId = parsed;
        }

        var rationale = "";
        var rationaleMatch = RationaleRegex().Match(raw);
        if (rationaleMatch.Success) rationale = rationaleMatch.Groups["r"].Value;

        recovered = (T)(object)new RouteDecisionDto(projectId, confidence, rationale);
        return true;
    }
}
