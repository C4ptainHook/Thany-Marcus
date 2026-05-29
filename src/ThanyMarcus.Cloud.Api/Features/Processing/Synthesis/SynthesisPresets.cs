using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class SynthesisPresetBodies
{
    public const string ZettelkastenVersion = "preset-zettelkasten-v1";
    public const string JournalVersion      = "preset-journal-v1";
    public const string EncyclopedicVersion = "preset-encyclopedic-v1";
    public const string TechnicalVersion    = "preset-technical-v1";

    private const string CommonGuardrails =
        "Hard rules:\n" +
        "- Do not invent facts, quotes, dates, names, numbers, or causal claims that are not present in the inputs.\n" +
        "- Use the canonical entity list below to wrap mentions as [[Wikilink]]. Never invent a wikilink target — only the names in the list may appear inside [[ ]].\n" +
        "- If an input failed (marked <input failed .../>), acknowledge it by presence (\"the attached image\") without inventing content.\n" +
        "- Output only the synthesized note body in Markdown — no frontmatter, no '## Sources' section. The server appends provenance separately.\n" +
        "- The first line MUST be a Markdown H1 heading that summarises the note: write `# ` followed by a concise 4–8 word descriptive title. Do not write the literal word \"Title\" — emit the actual summary text after the `#`.\n" +
        "- Each idea appears ONCE. Do not restate the same fact in different words. If you find yourself about to repeat something already written, stop and end the note instead.\n";

    private const string Zettelkasten =
        "You are writing a Zettelkasten-style atomic note in the user's voice.\n" +
        "Tone: first-person, present-tense, connecting. Short paragraphs. Each note states one idea\n" +
        "and links it to other ideas via [[wikilinks]] drawn from the entity list.\n" +
        "Aim for 4–10 sentences. Avoid encyclopedic framing; this is the user's working memory.\n";

    private const string Journal =
        "You are writing a dated journal entry in the user's voice.\n" +
        "Tone: first-person, narrative, reflective. Open with the situation, then the user's observation,\n" +
        "then implications. Use [[wikilinks]] from the entity list to reference people/projects/places.\n" +
        "Aim for 6–14 sentences.\n";

    private const string Encyclopedic =
        "You are writing a neutral, third-person summary of the inputs.\n" +
        "Tone: encyclopedic, factual, dispassionate. State what is known; do not editorialize.\n" +
        "Use [[wikilinks]] from the entity list when referencing entities. Aim for 4–10 sentences.\n";

    private const string Technical =
        "You are writing a terse, structured technical note.\n" +
        "Tone: precise, code-friendly. Prefer bullet points and short fenced blocks where useful.\n" +
        "Use [[wikilinks]] from the entity list for named libraries, components, and concepts.\n";

    public static string BodyFor(string preset) => preset switch
    {
        SynthesisPresets.Zettelkasten => Zettelkasten + "\n" + CommonGuardrails,
        SynthesisPresets.Journal      => Journal      + "\n" + CommonGuardrails,
        SynthesisPresets.Encyclopedic => Encyclopedic + "\n" + CommonGuardrails,
        SynthesisPresets.Technical    => Technical    + "\n" + CommonGuardrails,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "unknown preset"),
    };

    public static string VersionFor(string preset) => preset switch
    {
        SynthesisPresets.Zettelkasten => ZettelkastenVersion,
        SynthesisPresets.Journal      => JournalVersion,
        SynthesisPresets.Encyclopedic => EncyclopedicVersion,
        SynthesisPresets.Technical    => TechnicalVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "unknown preset"),
    };

    public static string AppendGuardrailsToCustom(string customBody) =>
        customBody.TrimEnd() + "\n\n" + CommonGuardrails;
}
