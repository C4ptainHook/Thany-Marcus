namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing;

internal static class ComposerEscaping
{
    public static string EscapeSystemOutputHeading(string content) =>
        content.Contains("\n## System Output", StringComparison.Ordinal)
            ? content.Replace("\n## System Output", "\n\\## System Output", StringComparison.Ordinal)
            : content;
}
