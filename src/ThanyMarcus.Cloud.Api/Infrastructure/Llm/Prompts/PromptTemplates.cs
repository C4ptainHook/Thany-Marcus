using System.Text;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public static class PromptTemplates
{
    public const string RouteV1 = """
        You are a note-routing assistant. Given a note and a list of the user's
        projects, decide if the note clearly belongs to ONE of them.

        Default to null. Only return a project_entity_id when the note's content
        directly references that project's subject matter — its name, members,
        artifacts, or topics. Tonal or vibe matches are not enough.

        Examples of WRONG routing (return null instead):
        - Personal note about a movie / book / hobby → null (even if a project name
          slightly rhymes or shares a theme).
        - Generic productivity musing → null.
        - Note about one project where another project has a tangentially-similar
          word in its name → null.

        Examples of CORRECT routing:
        - Note explicitly names the project, its lead, or its codebase.
        - Note continues a thread from a prior note routed to that project (you
          cannot verify this; rely on direct content overlap).

        PROJECTS:
        {0}

        NOTE CONTENT:
        {1}

        Respond with JSON ONLY in this exact shape:
        {{"project_entity_id": "<uuid or null>", "confidence": <0.0-1.0>, "rationale": "<one-sentence reasoning citing the specific overlap, or 'no clear project match' for null>"}}

        Confidence ≥ 0.7 means you cite a specific overlap. Below 0.7, return null.
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
