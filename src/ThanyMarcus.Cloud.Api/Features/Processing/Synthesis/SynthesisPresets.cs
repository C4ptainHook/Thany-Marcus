using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class SynthesisPresetBodies
{
    public const string ZettelkastenVersion = "preset-zettelkasten-v3";
    public const string JournalVersion      = "preset-journal-v3";
    public const string EncyclopedicVersion = "preset-encyclopedic-v3";
    public const string TechnicalVersion    = "preset-technical-v3";

    private const string CommonGuardrails =
        "Hard rules:\n" +
        "- Do not invent facts, quotes, dates, names, numbers, or causal claims that are not present in the inputs.\n" +
        "- Integrate facts from EVERY input (user notes, voice transcript, image caption, URL extract, file extract). Do not focus on only one input and ignore the others. Specific names, URLs, dates, numbers, and labels from any input must appear in the essence when relevant.\n" +
        "- Wrap the core ideas, names, projects, people, and places of the note in `[[ ]]` — e.g. `[[Slack]]` or `[[Customer Success]]`. Use your judgment over what's central to the note; no list is provided. The word \"Wikilink\" is NOT part of the syntax — never write `[[Wikilink:...]]` or `[[Wikilink]]`.\n" +
        "- If an input failed (marked <input failed .../>), acknowledge it by presence (\"the attached image\") without inventing content.\n" +
        "- The essence is a distillation of the inputs, never a re-narration. Each idea appears ONCE; do not restate the same fact in different words.\n";

    private const string Zettelkasten =
        "You are distilling the essence of a note in the user's voice.\n" +
        "Tone: first-person, present-tense, connecting. State the central idea\n" +
        "and link related concepts, names, and projects via [[wikilinks]] — your judgment.\n" +
        "Avoid encyclopedic framing; this is the user's working memory.\n";

    private const string Journal =
        "You are distilling the essence of a journal entry in the user's voice.\n" +
        "Tone: first-person, narrative, reflective. Capture the situation, the user's observation,\n" +
        "and the implication. Use [[wikilinks]] for people, projects, and places the entry touches.\n";

    private const string Encyclopedic =
        "You are distilling a neutral, third-person essence of the inputs.\n" +
        "Tone: encyclopedic, factual, dispassionate. State what is known; do not editorialize.\n" +
        "Use [[wikilinks]] for the named subjects and concepts under discussion.\n";

    private const string Technical =
        "You are distilling a terse, precise technical essence.\n" +
        "Tone: code-friendly and exact.\n" +
        "Use [[wikilinks]] for named libraries, components, and concepts.\n";

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
