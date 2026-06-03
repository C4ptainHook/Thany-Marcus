using System.Text;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class EssenceLayout
{
    public const string EssenceOpen = "%% thany:essence %%";
    public const string EssenceClose = "%% /thany:essence %%";
    public const string SourcesOpen = "%% thany:sources %%";
    public const string SourcesClose = "%% /thany:sources %%";
    public const string ProcessingOpen = "%% thany:processing %%";
    public const string ProcessingClose = "%% /thany:processing %%";

    public static string Assemble(
        string frontmatter,
        string essenceMarkdown,
        string? origin,
        string sources,
        string processingDetails)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        ArgumentNullException.ThrowIfNull(essenceMarkdown);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(processingDetails);

        var sb = new StringBuilder();
        sb.Append("---\n").Append(frontmatter.TrimEnd('\n')).Append("\n---\n\n");

        sb.Append(EssenceOpen).Append('\n');
        sb.Append(essenceMarkdown.TrimEnd()).Append('\n');
        sb.Append(EssenceClose).Append("\n\n");

        sb.Append("---\n\n");
        sb.Append("## Origin\n\n");
        sb.Append((origin ?? string.Empty).TrimEnd()).Append("\n\n");

        sb.Append(SourcesOpen).Append('\n');
        sb.Append(sources.TrimEnd()).Append('\n');
        sb.Append(SourcesClose).Append("\n\n");

        sb.Append(ProcessingOpen).Append('\n');
        sb.Append(processingDetails.TrimEnd()).Append('\n');
        sb.Append(ProcessingClose).Append('\n');

        return sb.ToString();
    }
}
