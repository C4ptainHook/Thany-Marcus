using System.Text;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public static class PromptTemplates
{
    public const string RouteV1 = """
        You are a note-routing assistant. Given a note's content and a list of
        projects, decide which project this note most belongs to (if any).

        PROJECTS:
        {0}

        NOTE CONTENT:
        {1}

        Decide which project, if any, this note belongs to. If no project is a
        clear match, return null for project_entity_id.

        Respond with JSON ONLY in this exact shape:
        {{"project_entity_id": "<uuid or null>", "confidence": <0.0-1.0>, "rationale": "<one-sentence reasoning>"}}
        /no_think
        """;

    public const string ExtractV1 = """
        You are an entity extraction assistant. Identify named entities mentioned
        in the note body below. For each mention, return:
          - the surface anchor text exactly as it appears
          - the start and end character offsets in the body (0-based, end-exclusive)
          - the entity kind (one of: person, organization, project, place, concept, other)
          - a canonical name (the entity's normalized form)
          - aliases (other surface forms that refer to the same entity)
          - confidence (0.0-1.0) of the extraction

        Be conservative. Prefer few high-quality mentions to many noisy ones.
        Never invent entities not present in the body.

        NOTE BODY:
        {0}

        Respond with JSON ONLY in this exact shape:
        {{"mentions": [
            {{"anchor_text": "<text>", "start_offset": <int>, "end_offset": <int>,
              "candidate_kind": "<kind>", "candidate_canonical": "<name>",
              "aliases": ["<alias>", ...], "confidence": <0.0-1.0>}}
        ]}}
        /no_think
        """;

    public const string DedupV1 = """
        You are an entity-dedup assistant. Given a candidate mention and a list
        of similar existing entities, decide whether the candidate is:
          - "alias_of": the same as one of the existing entities (give its id)
          - "new_entity": a genuinely new entity not present in the list
          - "ambiguous": cannot decide confidently (list the candidates considered)

        CANDIDATE:
        {0}

        SURROUNDING TEXT:
        {1}

        EXISTING SIMILAR ENTITIES:
        {2}

        Respond with JSON ONLY in this exact shape:
        {{"decision": "alias_of"|"new_entity"|"ambiguous",
          "matched_entity_id": "<uuid or null>",
          "candidates": ["<uuid>", ...],
          "confidence": <0.0-1.0>,
          "rationale": "<one-sentence reasoning>"}}
        /no_think
        """;

    public const string HubGenerateV1 = """
        You are a knowledge-base hub-note writer. Generate a concise Markdown
        dossier for the entity below, drawing exclusively from the surrounding
        text of the mentions provided. Use a "### Context" heading and brief
        bullet points; do not invent facts.

        ENTITY:
        {0}

        RECENT MENTIONS:
        {1}

        {2}

        Respond with Markdown ONLY (no preamble, no code fences).
        /no_think
        """;

    public static readonly CompositeFormat RouteV1Format = CompositeFormat.Parse(RouteV1);
    public static readonly CompositeFormat ExtractV1Format = CompositeFormat.Parse(ExtractV1);
    public static readonly CompositeFormat DedupV1Format = CompositeFormat.Parse(DedupV1);
    public static readonly CompositeFormat HubGenerateV1Format = CompositeFormat.Parse(HubGenerateV1);
}
