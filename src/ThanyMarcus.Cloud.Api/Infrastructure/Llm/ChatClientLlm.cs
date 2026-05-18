using System.Globalization;
using Microsoft.Extensions.AI;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class ChatClientLlm : ILlmClient, IAsyncDisposable
{
    private readonly IChatClient chatClient;
    private readonly LlmOptions options;

    public ChatClientLlm(IChatClient chatClient, string mode, string modelName, LlmOptions options)
    {
        this.chatClient = chatClient;
        this.options = options;
        Mode = mode;
        ModelName = modelName;
    }

    public string Mode { get; }
    public string ModelName { get; }

    public async Task<CompositeEnrichmentResult> EnrichCompositeAsync(
        CompositeEnrichmentRequest request, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, options.SystemPrompt),
            new(ChatRole.User,   BuildUserContent(request, options)),
        };

        var response = await chatClient
            .GetResponseAsync<EnrichmentSchema>(messages, cancellationToken: ct)
            .ConfigureAwait(false);

        var schema = response.Result;
        var bodyAnchors = MapAnchors(schema.BodyAnchors, request.Body.Length);

        var byAttachmentId = request.Attachments.ToDictionary(a => a.AttachmentId, a => a.ExtractedText.Length);
        var attachmentAnchors = new List<AttachmentAnchors>();
        foreach (var entry in schema.AttachmentAnchors)
        {
            if (!Guid.TryParse(entry.AttachmentId, out var id)) continue;
            if (!byAttachmentId.TryGetValue(id, out var len))   continue;
            var anchors = MapAnchors(entry.Anchors, len);
            if (anchors.Count > 0)
            {
                attachmentAnchors.Add(new AttachmentAnchors(id, anchors));
            }
        }

        return new CompositeEnrichmentResult(
            SuggestedProject:  schema.SuggestedProject,
            BodyAnchors:       bodyAnchors,
            AttachmentAnchors: attachmentAnchors,
            Tags:              schema.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
            InputTokens:       (int?)response.Usage?.InputTokenCount,
            OutputTokens:      (int?)response.Usage?.OutputTokenCount);
    }

    private static List<WikilinkAnchor> MapAnchors(IEnumerable<EnrichmentAnchor> source, int maxOffset)
    {
        var list = new List<WikilinkAnchor>();
        foreach (var a in source)
        {
            if (string.IsNullOrEmpty(a.Text) || string.IsNullOrEmpty(a.Target)) continue;
            if (a.Start < 0 || a.End <= a.Start || a.End > maxOffset) continue;
            list.Add(new WikilinkAnchor(a.Text, a.Start, a.End, a.Target));
        }
        return list;
    }

    private static string BuildUserContent(CompositeEnrichmentRequest req, LlmOptions options)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ci, $"{options.BodySectionHeader}");
        sb.AppendLine(ci, $"{req.Body}");
        if (req.Attachments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(ci, $"{options.AttachmentsSectionHeader}");
            foreach (var a in req.Attachments)
            {
                sb.AppendLine();
                var header = string.Format(ci, options.AttachmentHeaderTemplate, a.AttachmentId, a.Kind);
                sb.AppendLine(header);
                sb.AppendLine(ci, $"{a.ExtractedText}");
            }
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (chatClient is IAsyncDisposable ad) await ad.DisposeAsync();
        else if (chatClient is IDisposable d) d.Dispose();
    }
}
