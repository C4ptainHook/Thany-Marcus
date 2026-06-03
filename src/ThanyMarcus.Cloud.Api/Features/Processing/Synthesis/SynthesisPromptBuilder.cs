using System.Text;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class SynthesisPromptBuilder
{
    public static string Build(
        string systemBody,
        string? userBody,
        IReadOnlyList<SynthesisInput> attachmentInputs)
    {
        ArgumentNullException.ThrowIfNull(systemBody);
        ArgumentNullException.ThrowIfNull(attachmentInputs);

        var sb = new StringBuilder();
        sb.AppendLine(systemBody.TrimEnd());
        sb.AppendLine();

        sb.AppendLine("Inputs:");
        sb.AppendLine();
        sb.AppendLine("User notes:");
        if (string.IsNullOrWhiteSpace(userBody))
        {
            sb.AppendLine("<empty/>");
        }
        else
        {
            sb.AppendLine(userBody.Trim());
        }
        sb.AppendLine();

        foreach (var input in attachmentInputs)
        {
            if (input.Mode == AttachmentMode.Reference)
            {
                continue;
            }

            if (input.Mode == AttachmentMode.Metadata)
            {
                sb.Append("<reference id=\"").Append(input.Id ?? "att")
                  .Append("\" title=\"").Append(EscapeAttr(input.Title ?? ""))
                  .Append("\" description=\"").Append(EscapeAttr(input.Description ?? ""))
                  .Append("\" url=\"").Append(EscapeAttr(input.Url ?? ""))
                  .AppendLine("\"/>");
                sb.AppendLine();
                continue;
            }

            var heading = input.Kind switch
            {
                "voice" => "Voice transcript:",
                "image" => "Image caption:",
                "url"   => "URL extract:",
                "file"  => "File extract:",
                _       => $"{input.Kind} extract:",
            };
            sb.AppendLine(heading);
            if (input.Content is null)
            {
                sb.Append("<input failed kind=\"").Append(input.Kind).Append("\" reason=\"")
                  .Append(EscapeAttr(input.FailureReason ?? "unknown"))
                  .AppendLine("\"/>");
            }
            else
            {
                sb.AppendLine(input.Content.Trim());
            }
            sb.AppendLine();
        }

        sb.AppendLine("Write the synthesized note now.");
        sb.AppendLine("/no_think");
        return sb.ToString();
    }

    private static string EscapeAttr(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
}
