namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public static class VlmPromptBuilder
{
    public const string Prompt = """
        Describe what is shown in this image in plain English.

        Cover the subject, the context or setting, any visible text or labels, and notable details (people, UI elements, layout, charts, code, diagrams, etc.).

        Respond in 2–6 sentences as one paragraph. Do not output JSON, bullet lists, headers, or code fences. Do not editorialize or speculate beyond what is visible. Do not mention EXIF data, file information, or the photographer.
        """;

    public static string Build() => Prompt;
}
