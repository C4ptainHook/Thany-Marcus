using System.Globalization;
using System.Text.RegularExpressions;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Composing.Renderers;

public sealed partial class FailedHiddenRenderer
{
    [GeneratedRegex(@"[\r\n\-]+")]
    private static partial Regex ReasonScrubber();

    public string Render(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var rawReason = string.IsNullOrWhiteSpace(attachment.ExtractionError)
            ? "unknown"
            : attachment.ExtractionError!;
        var reason = ReasonScrubber().Replace(rawReason, " ").Trim();
        if (reason.Length == 0) reason = "unknown";
        return string.Format(
            CultureInfo.InvariantCulture,
            "<!-- thany-marcus:attachment id={0} kind={1} status={2} reason={3} -->\n",
            attachment.Id,
            attachment.Kind,
            attachment.ExtractionStatus,
            reason);
    }
}
