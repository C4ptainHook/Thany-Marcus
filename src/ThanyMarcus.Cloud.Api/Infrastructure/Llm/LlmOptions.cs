namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class LlmOptions
{
    public string DefaultAnthropicModel { get; init; } = "claude-haiku-4-5-20251001";
    public string DefaultOpenAiModel    { get; init; } = "gpt-4o-mini";

    public string SystemPrompt { get; init; } = "";
    public string BodySectionHeader        { get; init; } = "## Body";
    public string AttachmentsSectionHeader { get; init; } = "## Attachments";
    public string AttachmentHeaderTemplate { get; init; } = "### Attachment {0} (kind={1})";
}
