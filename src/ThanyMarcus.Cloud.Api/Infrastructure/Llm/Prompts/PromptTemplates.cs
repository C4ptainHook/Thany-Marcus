using System.Text;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

public static class PromptTemplates
{
    public const string RouteV1 = """
        You are a note-routing assistant. Given a note and a list of the user's
        vault folders, decide which folder it clearly belongs to.

        Default to null (the note goes to Inbox). Only return a folder when the
        note's content directly references its subject — its name, its members,
        its artifacts, or its topics.

        Examples of WRONG routing (return null instead):
        - Personal note about a movie / book / hobby → null (even if a folder
          name slightly rhymes or shares a theme).
        - Generic productivity musing → null.
        - Note about one topic where another folder has a tangentially-similar
          word in its name → null.

        FOLDERS:
        {0}

        NOTE CONTENT:
        {1}

        Respond with JSON ONLY in this exact shape:
        {{"folder": "<exact folder name or null>", "confidence": <0.0-1.0>, "rationale": "<one-sentence reasoning citing the specific overlap, or 'no clear folder match' for null>"}}

        Confidence ≥ 0.7 means you cite a specific overlap. Below 0.7, return null.
        /no_think
        """;

    public const string ExtractV1 = """
        You are an entity extraction assistant. Extract ONLY proper-noun named
        entities from the note body below: specific people, organizations,
        places, and named products or projects.

        Do NOT extract generic noun phrases, activities, topics, or descriptions.
        For example, do NOT extract "index funds", "monthly contributions",
        "stock picking", or "note-taking" — these are not named entities.

        For each mention, return:
          - anchor_text: the 1-4 word span where the name appears, exactly as
            written. It is the NAME only, never the surrounding sentence.
            In "She wants the OpenAI integration shipped", the anchor is
            "OpenAI" — not the whole sentence.
          - the start and end character offsets of that span (0-based, end-exclusive)
          - the entity kind (one of: person, organization, place, concept, other)
          - a canonical name (the entity's normalized form)
          - aliases (other proper-noun surface forms of the same entity, e.g.
            "Київ" for "Kyiv"; never a sentence or description)
          - confidence (0.0-1.0) of the extraction

        Use "concept" only for named concepts (e.g. Zettelkasten, Bauhaus), never
        common-noun topics. Avoid "other"; prefer not extracting over a vague kind.

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
