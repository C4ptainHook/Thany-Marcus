# CLOUD-ENTITY-SUGGESTIONS — Human-in-the-loop entity curation

**Goal:** replace silent auto-creation of entities with a *suggestion* surface. The LLM observes candidate mentions across notes; the user decides what becomes a canonical entity. Wikilinks in note bodies become pure synth judgment, independent of the entity table. On acceptance, the cloud materialises the entity as a stub `Entities/<Canonical>.md` file in the user's vault with `aliases:` frontmatter, so Obsidian's native resolver folds drifting surface forms (`[[Mike]]`, `[[Michael Jackson]]`, `[[MJ]]`) into the same graph node without any synth-side rewriting. Estimated **2.5–3 person-days** with AI-agent assistance.

## Why this exists

The current pipeline (`ExtractingEntitiesHandler.cs`) does extract → kNN → dedup-LLM → auto-persist with `IsProvisional=true`. Observed reality after weeks of submissions:

- `entities` table on the live droplet contains 3 user-created Projects and **0 auto-created rows** across every note ever ingested
- `mentions` table is empty for the same reason
- Dedup gates (`DedupNewMin: 0.6`, `DedupAliasMin: 0.8`) drop nearly every candidate from a 1.7B model with little context
- The synth prompt is told `"Available entities (use ONLY these for [[wikilinks]])"` — so an empty entity table means notes ship with **zero wikilinks**
- The provisional → canonical promotion UI was never built; auto-persisted rows would be invisible even if they landed

So the pipeline costs ~2 LLM calls per note and produces nothing the user sees.

A second flaw exposed by user grilling 2026-05-30: wikilinks in note bodies are **conceptually independent** of the entity DB. In Obsidian, `[[Michael Jackson]]` in two notes is enough to connect them in the graph view — no DB row required. The current "synth may only wikilink names from the canonical list" rule was a category error that conflated DB-side entity graph with body-side wikilink graph.

A third problem surfaces once wikilinks are decoupled: **surface-form drift**. With synth picking its own anchor text per-note, the same person/topic can appear as `[[Michael Jackson]]` in one note and `[[Mike]]` in another. Obsidian treats those as different files; the graph stays disconnected.

The clean answer is not to rewrite the synth output — it is to use **Obsidian's own alias mechanism**. When the user accepts a suggestion, the cloud writes a stub file at `Entities/Michael Jackson.md` with `aliases: [Mike, MJ, Jackson]` in frontmatter. From that point on, every variant `[[X]]` in any note resolves to the same file in Obsidian's graph view and backlinks panel — natively, without any cloud-side rewrite.

This ticket fixes all three problems:

1. **Decouple wikilinks from entity DB** — synth wraps core ideas in `[[ ]]` by judgment alone.
2. **Replace auto-persist with suggestions** — extracted candidates accumulate in a suggestion table; the user accepts/dismisses; only accepted candidates become canonical Entity rows.
3. **Materialise accepted entities as alias-stub markdown files** — surface drift is handled by Obsidian's native resolver, not by the LLM or a normaliser prompt.

See [[feedback_wikilinks_independent]].

## Scope

**In scope:**
- Drop the "use ONLY these for `[[wikilinks]]`" constraint from `SynthesisPromptBuilder` and `SynthesisPresetBodies.CommonGuardrails`. Drop the `entityCanonicalNames` parameter from `Build()`. Bump preset versions (e.g. `preset-zettelkasten-v2`) to invalidate caches.
- Reframe per-preset wikilink language: `"Use [[wikilinks]] for the core ideas, names, projects, people, and places this note discusses — your judgment, no list required."`
- Replace `ExtractingEntitiesHandler`'s auto-persist branch with insert-into-suggestion-table. Keep extract LLM. Keep kNN against existing entities. Remove dedup-LLM call and its thresholds.
- New table `entity_suggestions` (schema below) with promotion lifecycle.
- New endpoints: `GET /api/entity-suggestions` (list), `POST /api/entity-suggestions/{id}/accept`, `POST /api/entity-suggestions/{id}/dismiss`, `POST /api/entity-suggestions/{id}/edit` (rename canonical or edit aliases before accepting).
- New plugin sidebar panel `EntitySuggestionsPanel` listing suggestions above the threshold, with Accept / Dismiss / Edit per row + a count badge on the panel header.
- **On Accept: cloud writes `Entities/<Canonical>.md` to the user's vault** with `aliases:` frontmatter populated from the suggestion's aliases. Obsidian's resolver matches every variant `[[Mike]]`, `[[MJ]]`, `[[Jackson]]` to this single file. Stub is cloud-owned: editing alias frontmatter happens through the suggestion-edit + accept flow, not by hand-editing the file.
- **On Edit-after-Accept** (user adds an alias later via the suggestion edit flow): cloud rewrites the stub's `aliases:` frontmatter in place. Body content (which is empty by default) is preserved if present.
- Remove `IsProvisional` flag from `Entity` and the table — every row is now user-confirmed.
- Backend tests for suggestion aggregation, threshold gating, accept/dismiss/edit flows.
- Plugin tests for the panel render, badge count, and Accept → Entity-row roundtrip.

**Out of scope:**
- Alias-of suggestions ("seen 'Mike' alongside 'Michael Jackson' in 3 notes — same person?"). Land in a follow-up after canonical-suggest is validated.
- Synth-prompt alias normaliser. The Obsidian stub-file approach (in scope above) makes prompt-side normalisation redundant — Obsidian's resolver handles aliases natively, the LLM doesn't need to know.
- Hub note content generation. Stub files have ONLY `aliases:` frontmatter, no body content; auto-generating a dossier of all mentions is a separate ticket (user already deprioritised in prior grilling).
- Backfilling suggestions from notes already ingested. New behaviour applies to notes ingested *after* deployment; old notes are not re-extracted.
- Mention spans (start/end offsets). Wikilinks ARE the spans in an Obsidian-native system; the column is redundant. Removal is a separate cleanup.
- Per-kind suggestion filters in v1 (e.g. "only suggest Person/Place, skip Organization"). All extracted kinds surface; user dismisses what they don't want.
- Cross-note dedup of dismissed suggestions ("dismissed 'API' once — never suggest again across any note"). v1 dismiss is per-suggestion-row.
- Rewriting `[[surface]]` text in past notes when a new alias is added. The whole point of stub files is that Obsidian's resolver handles drift without rewriting; past notes keep their original surface forms and link via aliases.

## State machine semantics

```
entity_suggestion lifecycle:

  observed          ← extract LLM emitted this candidate from a note
  surfaceable       ← occurrence_count >= SuggestionThreshold (default 3)
                      AND distinct_note_count >= 2
                      AND dismissed_at IS NULL
                      AND accepted_at IS NULL
  accepted          ← user clicked Accept; entities row created;
                      suggestion.accepted_at + accepted_entity_id set
  dismissed         ← user clicked Dismiss; suggestion.dismissed_at set;
                      never re-surfaces
```

Observed → surfaceable is a query-time predicate, not a status column — the row exists from first observation but the API only returns rows that meet the threshold.

Accept side effects:
- Create `Entity` row from the suggestion (canonical name, kind, aliases as initial alias set).
- Run granite embedding on canonical name + aliases; persist to `entities.embedding` for kNN matching of future candidates.
- **Generate stub markdown** at `Entities/<sanitised canonical>.md` and persist as a `Note` row with `kind = entity_stub` so the existing plugin sync mechanism picks it up automatically. Content:
  ```
  ---
  aliases: [Mike, MJ, Jackson]
  thany:entity_id: <uuid>
  thany:kind: person
  ---
  ```
- Path conflict handling: if `Entities/<canonical>.md` already exists in `notes` (user has a note there, or a previous entity stub for a renamed entity), the Accept call returns 409 with `{ conflict: "path", existing_note_id: ... }`. Plugin prompts user to rename the canonical before retrying.
- Mark the suggestion `accepted_at = NOW()`, `accepted_entity_id = <new entity id>`.
- All future occurrences of this candidate text (or close kNN matches) land as `mentions` rows against the now-canonical entity, NOT as new suggestions.

Edit-after-Accept side effects (user adds a new alias via the same edit flow):
- Append alias to `entities.aliases`.
- Rewrite the stub markdown's `aliases:` frontmatter line in place. Body (everything after the closing `---`) is preserved untouched in case the user has added their own content.
- Sync pushes the updated stub to the plugin → vault.

Dismiss side effects:
- Set `dismissed_at = NOW()`. No further surfacing for this exact suggestion.
- Does NOT prevent re-creation of a fresh suggestion if the same text appears in *new* notes (could be future-work tightened).

## Concrete files

### Database

#### Migration `0NNN_entity_suggestions.sql`

```sql
CREATE TABLE entity_suggestions (
    id                  UUID PRIMARY KEY,
    canonical_text      TEXT NOT NULL,
    kind                TEXT NOT NULL,         -- person | organization | project | place | concept | other
    aliases             TEXT[] NOT NULL DEFAULT '{}',
    occurrences         JSONB NOT NULL,        -- [{note_id, anchor_text, surrounding_text, observed_at}]
    occurrence_count    INTEGER NOT NULL DEFAULT 0,
    distinct_note_count INTEGER NOT NULL DEFAULT 0,
    first_seen_at       TIMESTAMPTZ NOT NULL,
    last_seen_at        TIMESTAMPTZ NOT NULL,
    accepted_at         TIMESTAMPTZ,
    accepted_entity_id  UUID REFERENCES entities(id),
    dismissed_at        TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_entity_suggestions_surfaceable
    ON entity_suggestions (occurrence_count DESC, last_seen_at DESC)
    WHERE accepted_at IS NULL AND dismissed_at IS NULL;

CREATE UNIQUE INDEX idx_entity_suggestions_canonical_kind
    ON entity_suggestions (LOWER(canonical_text), kind)
    WHERE accepted_at IS NULL AND dismissed_at IS NULL;
```

Aggregation key is `(LOWER(canonical_text), kind)` — same anchor surface form, same kind = same suggestion row, occurrence appended. kNN-near matches (e.g. "Mike" embedding close to existing "Michael Jackson" suggestion) merge into the existing row by appending the new alias to `aliases`.

#### Migration `0NNN_drop_entity_is_provisional.sql`

```sql
-- Auto-creation of provisional entities is removed; only user-confirmed rows exist.
DELETE FROM entities WHERE is_provisional = TRUE;
ALTER TABLE entities DROP COLUMN is_provisional;
```

Destructive on the dev droplet; safe today because no provisional rows exist (empty `entities.is_provisional = true` result set, verified 2026-05-30).

### Backend

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/ExtractingEntitiesHandler.cs` — REWRITE

Current behaviour:
```
for each extracted mention:
    candidates = kNN against entities (top 5)
    decision = DedupLlm.Classify(mention, candidates)        ← REMOVE
    if decision == alias_of && confidence >= 0.8: add mention to existing entity
    if decision == new_entity && confidence >= 0.6:          ← REMOVE
        create entity with IsProvisional=true; add mention
```

New behaviour:
```
for each extracted mention:
    candidates = kNN against entities (top 5)
    if any candidate within cosine distance <= 0.25:
        # confident kNN match against a user-curated entity — persist mention directly
        create Mention row pointing at the matched entity
    else:
        # not a known entity → accumulate as suggestion
        candidates_in_suggestions = kNN against entity_suggestions.canonical_text embedding (top 5)
        if any suggestion within distance <= 0.25:
            append occurrence to that suggestion;
            occurrence_count++; recompute distinct_note_count;
            add anchor_text to suggestion.aliases if novel
        else:
            insert new entity_suggestion row with this occurrence
```

The kNN gate (`distance <= 0.25`) replaces the dedup-LLM's `alias_of` decision. Tunable in config under `LlmIntelligence:SuggestionMatchDistance`. The deterministic kNN is much cheaper and more predictable than the LLM call it replaces.

Remove the `DedupLlmClient.ClassifyAsync` call entirely from this handler.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPresetBodies.cs` — EDIT

```csharp
private const string CommonGuardrails =
    "Hard rules:\n" +
    "- Do not invent facts, quotes, dates, names, numbers, or causal claims that are not present in the inputs.\n" +
    "- Integrate facts from EVERY input ...\n" +
    "- Wrap the core ideas, names, projects, people, and places of the note in `[[ ]]` — e.g. `[[Slack]]` or `[[Customer Success]]`. Use your judgment over what's central to the note. The word \"Wikilink\" is NOT part of the syntax — never write `[[Wikilink:...]]`.\n" +
    "- If an input failed ...\n" +
    "- Output only the synthesized note body in Markdown ...\n" +
    "- The first line MUST be a Markdown H1 heading ...\n" +
    "- Each idea appears ONCE ...\n";
```

Drop "Only names from the canonical entity list above may appear inside `[[ ]]`."

Update per-preset wikilink hints:
- Zettelkasten: `"...links one idea to others via [[wikilinks]] for core concepts, names, and projects."`
- Journal: `"...uses [[wikilinks]] for people, projects, and places the entry touches."`
- Encyclopedic: `"...uses [[wikilinks]] for the named subjects and concepts under discussion."`
- Technical: `"...uses [[wikilinks]] for named libraries, components, and concepts."`

Bump version constants:
```csharp
public const string ZettelkastenVersion = "preset-zettelkasten-v2";
public const string JournalVersion      = "preset-journal-v2";
public const string EncyclopedicVersion = "preset-encyclopedic-v2";
public const string TechnicalVersion    = "preset-technical-v2";
```

The version bump forces cache invalidation on next reprocess of any note (synth cache key includes `promptVersion`).

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPromptBuilder.cs` — EDIT

Drop the `entityCanonicalNames` parameter and the "Available entities (use ONLY these for `[[wikilinks]]`)" section. Signature:

```csharp
public static string Build(
    string systemBody,
    string? userBody,
    IReadOnlyList<SynthesisInput> attachmentInputs)
```

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/SynthesizingHandler.cs` — EDIT

- Delete `LoadCanonicalEntityNamesAsync` method.
- Delete the call: `var entityNames = await LoadCanonicalEntityNamesAsync(note.Id, ct);`
- Delete `mentionsHash` from cache key computation. `ComputeCacheKey` signature drops the parameter.
- Update `SynthesisPromptBuilder.Build` call site to match new signature.

#### `src/ThanyMarcus.Cloud.Api/Features/EntitySuggestions/` — NEW directory

```
EntitySuggestionsEndpoints.cs    // GET / accept / dismiss / edit
EntitySuggestion.cs              // EF Core entity
EntitySuggestionRepository.cs    // queries + threshold filter
EntitySuggestionAggregator.cs    // append-or-create logic called from ExtractingEntitiesHandler
```

Endpoints:

```csharp
app.MapGet ("/api/entity-suggestions",                  ListAsync);
app.MapPost("/api/entity-suggestions/{id}/accept",      AcceptAsync);
app.MapPost("/api/entity-suggestions/{id}/dismiss",     DismissAsync);
app.MapPost("/api/entity-suggestions/{id}/edit",        EditAsync);
```

All gated by `RequirePluginAuthFilter` (same as existing note endpoints).

`ListAsync` returns surfaceable suggestions (count ≥ threshold, ≥ 2 distinct notes, not accepted, not dismissed), ordered by `occurrence_count DESC, last_seen_at DESC`, top 50.

`AcceptAsync`: creates `Entity` row → embeds canonical name → **generates stub `Note` row at `Entities/<sanitised canonical>.md`** → marks suggestion accepted → idempotent (returns 200 with the existing `entity_id` if already accepted). Returns 409 if a different `Note` already occupies the target path (see "Stub file lifecycle" below).

`EditAsync` body: `{ canonical_text?: string, aliases?: string[] }`. Mutates the suggestion in place. Common case: user wants to rename "Mj" to "Michael Jackson" before accepting. If the suggestion is already accepted, also updates the linked entity and rewrites the stub `Note` content (aliases frontmatter line only — body preserved).

#### `src/ThanyMarcus.Cloud.Api/Features/EntitySuggestions/EntityStubWriter.cs` — NEW

Encapsulates stub-file generation and updates. Responsibilities:

- `BuildStubMarkdown(string canonical, string kind, IReadOnlyList<string> aliases, Guid entityId)` — returns the full file content.
- `ComputeStubRelativePath(string canonical)` — applies the same filename sanitiser used by `SynthesizingHandler.ComputeRelativePath` (Obsidian-illegal chars stripped) and prefixes `Entities/`. Returns e.g. `Entities/Michael Jackson.md`.
- `CreateAsync(Entity entity, IReadOnlyList<string> aliases, CancellationToken ct)` — inserts a `Note` row with `kind = entity_stub`, `body_output = BuildStubMarkdown(...)`, `relative_path = ComputeStubRelativePath(...)`, `status = ready` (skips the saga entirely — no synth, no embed needed for a stub). Throws `StubPathConflictException` if the path is taken by a non-stub note.
- `UpdateAliasesAsync(Entity entity, IReadOnlyList<string> newAliases, CancellationToken ct)` — finds the stub Note linked by `entity.StubNoteId`, rewrites the `aliases:` line in its frontmatter, leaves the body untouched, bumps `updated_at` so the plugin sync picks it up.

Stub frontmatter format (deterministic):
```
---
aliases:
  - Mike
  - MJ
  - Jackson
thany:entity_id: 019e7d3a-...
thany:kind: person
---

```

Trailing blank line for cleanliness. `thany:entity_id` lets the cloud find the stub when the user adds aliases later, even if the file was renamed in Obsidian.

The `notes` table gains a column `kind TEXT NOT NULL DEFAULT 'synth_note'`. Possible values: `synth_note`, `entity_stub`. The plugin sync API filters/treats them uniformly today (both are markdown files with frontmatter); a follow-up can add per-kind UI affordances.

#### Migration `0NNN_notes_kind_column.sql`

```sql
ALTER TABLE notes ADD COLUMN kind TEXT NOT NULL DEFAULT 'synth_note';
CREATE INDEX idx_notes_kind ON notes (kind) WHERE deleted_at IS NULL;
```

#### Entity → stub linkage

```sql
ALTER TABLE entities ADD COLUMN stub_note_id UUID REFERENCES notes(id);
CREATE UNIQUE INDEX idx_entities_stub_note ON entities (stub_note_id) WHERE stub_note_id IS NOT NULL;
```

One stub per accepted entity. Lets the cloud find the stub on alias-edit without a path lookup (which would break on rename).

#### `src/ThanyMarcus.Cloud.Api/appsettings.json` — EDIT

```json
"LlmIntelligence": {
  "Thresholds": {
    "RouteAcceptMin": 0.7,
    "MentionMin": 0.6,
    "SuggestionMatchDistance": 0.25
  },
  // remove DedupAliasMin, DedupNewMin
  "EntitySuggestions": {
    "OccurrenceThreshold": 3,
    "DistinctNoteThreshold": 2,
    "StubsFolder": "Entities"
  }
}
```

`StubsFolder` controls the vault subfolder for accepted-entity stub files. Defaults to `Entities`. A user who already organises their vault differently can change it; the cloud only owns this folder.

#### `src/ThanyMarcus.Shared.PluginApi/EntitySuggestions.cs` — NEW

DTOs for `RelatedNotesItem`-style records: `EntitySuggestionDto`, `EditEntitySuggestionRequest`, etc. Shared between cloud-api and plugin.

### Plugin

#### `plugin/thany-marcus/src/api.ts` — ADD

```ts
interface EntitySuggestion {
    id: string;
    canonicalText: string;
    kind: string;
    aliases: string[];
    occurrenceCount: number;
    distinctNoteCount: number;
    firstSeenAt: string;
    lastSeenAt: string;
    sampleOccurrence: { noteId: string; anchorText: string; surroundingText: string };
}

listEntitySuggestions(signal?: AbortSignal): Promise<EntitySuggestion[]>
acceptEntitySuggestion(id: string): Promise<{ entityId: string }>
dismissEntitySuggestion(id: string): Promise<void>
editEntitySuggestion(id: string, patch: { canonicalText?: string; aliases?: string[] }): Promise<void>
```

#### `plugin/thany-marcus/src/sidebar/EntitySuggestionsPanel.ts` — NEW

Sits below the existing Projects panel (or as a sibling tab — pick one layout). Panel header shows count badge (e.g. "Suggestions (4)").

Each row: canonical text + kind chip + occurrence count + sample anchor in faint type + `[Accept] [Edit] [Dismiss]` buttons.

`Edit` opens a small inline form: rename canonical, edit aliases (comma-separated). Save updates the suggestion; user can then Accept.

`Accept` calls API; on 200 removes the row optimistically and refreshes the Projects/Entities sidebar (the new entity may want to surface in routing-projects too if `kind=project`).

Refresh trigger: panel polls the API on plugin start, on focus, and on a 60s interval. SSE notification for `entity_suggestion_surfaced` events is a nice-to-have but not required for v1.

#### `plugin/thany-marcus/src/main.ts` — wire panel mount

Same pattern as the existing Projects panel mount.

### SSE (optional)

`event: entity_suggestion_surfaced` fired from `EntitySuggestionAggregator` when a suggestion's `occurrence_count` crosses the threshold for the first time. Payload `{ suggestionId, canonicalText, kind }`. The plugin can then refresh the panel + flash the badge.

Skip this in v1 if it adds friction; the 60s poll covers latency well enough for capture flow.

## Stub file lifecycle

The stub file is the bridge between the cloud's entity records and Obsidian's native alias resolver. Without it, surface drift (`[[Mike]]` vs `[[Michael Jackson]]`) splits the graph; with it, every variant resolves to one node natively.

**Format** (deterministic, regenerable):

```
---
aliases:
  - Mike
  - MJ
  - Jackson
thany:entity_id: 019e7d3a-...
thany:kind: person
---

```

Empty body by default. The cloud owns the frontmatter; the user may add their own body content below it (the cloud preserves anything after the closing `---` line on alias updates).

**Path:** `Entities/<sanitised canonical>.md`. Same sanitiser as synth notes (`SynthesizingHandler.ComputeRelativePath`). Folder is configurable via `LlmIntelligence:EntitySuggestions:StubsFolder`, default `Entities`.

**Lifecycle:**

| Trigger | Action |
|---|---|
| Accept suggestion | Cloud inserts `Note` row with `kind=entity_stub`, `relative_path=Entities/X.md`, `body_output=<stub markdown>`, `status=ready`. Plugin sync writes file to vault on next pull. |
| Edit-after-Accept (add alias) | Cloud updates `entities.aliases`, rewrites the `aliases:` frontmatter in the linked stub note, bumps `updated_at`. Body preserved. Plugin sync re-writes file. |
| Edit-after-Accept (rename canonical) | Cloud detects path change; **refuses** by default (returns 409). Renaming a canonical means moving an Obsidian file, which breaks all existing `[[X]]` links. User must do it manually in Obsidian if they want this, then re-sync the alias list. (Future ticket: opt-in cloud-side rename + bulk wikilink rewrite.) |
| User deletes the stub file in Obsidian | Plugin's local change pushes to cloud → cloud detects orphaned entity (entity.stub_note_id points at a soft-deleted note) → entity is marked dormant: it no longer surfaces as a kNN candidate or contributes to alias resolution, but mentions historically linked to it remain. User can resurrect by accepting the suggestion again. |
| User renames the file in Obsidian | The `thany:entity_id` frontmatter field is the source of truth. On sync, the cloud updates `notes.relative_path` to the new location. Aliases continue to work because Obsidian resolves by file content, not by filename. |
| Path conflict on Accept | If `Entities/X.md` already holds a `synth_note` or a different `entity_stub`, Accept returns 409 with `{ conflict: "path", existing_note_id, existing_kind }`. Plugin asks user to rename the canonical (via the Edit form) and retry. |

**Why stubs as `Note` rows instead of a new table:** the plugin sync code path already handles `Note` rows end-to-end (download, frontmatter parse, write to vault, conflict resolution). Adding a parallel `entity_files` table would duplicate that machinery. `kind=entity_stub` is the smallest discriminator that lets future code branch on the type if needed (e.g., to hide stubs from the queue UI).

**Why frontmatter `thany:entity_id` instead of filename-based mapping:** filenames are user-mutable (Obsidian rename), so they cannot be the linkage key. The frontmatter field is invisible to the user but stable across renames.

## Tests

### Backend
- `EntitySuggestionAggregatorTests`: first observation creates row; same anchor in second note increments count + distinct-note count; kNN-near anchor in third note appends as alias not new row.
- `EntitySuggestionsEndpointTests`: GET respects threshold; accept idempotent; accept creates Entity + embedding; dismiss soft-deletes.
- `EntityStubWriterTests`: BuildStubMarkdown produces deterministic frontmatter; ComputeStubRelativePath sanitises Obsidian-illegal chars + applies the configured folder; CreateAsync inserts a `kind=entity_stub` Note linked via `entities.stub_note_id`; UpdateAliasesAsync rewrites the `aliases:` block and preserves arbitrary body content below; path conflict throws `StubPathConflictException`.
- `SynthesisPromptBuilderTests`: built prompt no longer contains "Available entities" header; wikilink language is judgment-based.
- `SynthesizingHandlerTests`: cache key stable across runs without entity-list input.
- `ExtractingEntitiesHandlerTests`: kNN match within distance threshold creates Mention; outside threshold creates suggestion; no auto-Entity creation in any path.

### Plugin
- `EntitySuggestionsPanel.test.ts`: renders rows; Accept removes row + refetches; Edit submits patch; Dismiss soft-removes.
- Sync round-trip test (or manual smoke): Accept → stub file appears in vault at `Entities/X.md` with correct frontmatter → `[[Mike]]` in another note resolves to it in Obsidian's link autocomplete.

## Migration / deployment notes

- Apply migrations in order: `notes_kind_column`, `entity_suggestions`, `entities_stub_note_link`, `drop_entity_is_provisional`.
- Synth cache key change (preset version bump + `mentionsHash` removal) invalidates ALL existing synth caches — first reprocess of any note will burn an LLM call. Acceptable; cache will rewarm naturally.
- Plugin needs a rebuild + copy to the demo vault. The new panel won't appear until the user reloads Obsidian. Existing notes are untouched on first sync after deploy; stub files only appear as the user accepts suggestions.
- The `Entities/` folder is created lazily on first Accept. If the user has chosen a different `StubsFolder` config, it must exist (or the plugin's sync layer must mkdir on first write).
- No data loss: every existing user-created Entity row is preserved (only `is_provisional=true` rows are deleted, currently zero).

## Risks / open questions

- **Threshold tuning.** `OccurrenceThreshold=3` over `DistinctNoteThreshold=2` is a guess. Too high → user never sees suggestions; too low → spam. Tune after first week of real submissions.
- **kNN false-merge.** Distance 0.25 against `entities.embedding` could over-match (e.g. "Slack" and "Discord" might be too close in granite-256 space). If observed, drop to 0.20 or add a kind-must-match predicate.
- **Suggestion-vs-mention race.** If a user accepts a suggestion mid-ingest, the in-flight note's mentions might still target the suggestion row that no longer exists. Mitigate by writing mentions during the *current* phase only after suggestion-aggregation completes, never holding references across phases.
- **Wikilink quality without the canonical list.** Synth model's judgment of "core ideas" may be noisier than the previous list-bounded approach. Observe; if too noisy, the fix is prompt-engineering (preset bodies), not re-coupling to the entity DB.
- **Stub file user-ownership ambiguity.** Cloud owns the frontmatter; user may add body content. The "preserve body, rewrite frontmatter" merge logic on alias updates is the touchy bit — needs a precise frontmatter parser, not a string replace, or user body content can be lost. Use the same frontmatter library the synth pipeline already uses.
- **Sync conflict if user edits aliases manually in Obsidian.** User edits `Entities/Michael Jackson.md` frontmatter, adds an alias, syncs back to cloud. Cloud sees `aliases:` changed and updates `entities.aliases`. Round-trip safe IF the sync layer treats the file as bidirectional for the `aliases:` field. v1 simplification: cloud is one-way authoritative on stub frontmatter; manual edits are overwritten on next cloud-side update. Add bidirectional sync of stub aliases in a follow-up if user demand emerges.
- **Stub creates a tight coupling between cloud DB and Obsidian filesystem.** A vault deletion or a sync misconfiguration loses stub files; the entity DB stays correct but `[[Mike]]` stops resolving. Mitigation: a "rebuild stubs" CLI/endpoint that regenerates `Entities/*.md` from the current `entities` table.

## Done = ?

1. Submit a note mentioning "Michael Jackson" three times across two new notes. `entity_suggestions` table has a row with `occurrence_count >= 3`, `distinct_note_count >= 2`. Sidebar panel shows it with count badge "1".
2. Click Accept. `entities` table has a new row with `canonical_text='Michael Jackson'`, `kind='person'`, `stub_note_id` set. Suggestion row marked `accepted_at` not null.
3. **A new file `Entities/Michael Jackson.md` appears in the vault** within one sync cycle, containing only `aliases: [Mike, Jackson, ...]` + `thany:entity_id` + `thany:kind` frontmatter. Body is empty.
4. **In Obsidian, typing `[[Mike` autocompletes to `Michael Jackson` from the alias.** Opening any note containing `[[Mike]]` or `[[Jackson]]` follows the link to `Entities/Michael Jackson.md`. The graph view shows all variants as one node.
5. Submit a fourth note where the user writes "Mike said hi". Synth produces `[[Mike]]` (its judgment, not normalised). `mentions` table gets a new row tied to the accepted entity (via kNN-near match against the canonical embedding). Obsidian's graph still shows the connection because the stub file's `aliases:` resolves `[[Mike]]` to `[[Michael Jackson]]`.
6. Edit the suggestion (post-Accept) to add alias "MJ". `entities.aliases` updates. `Entities/Michael Jackson.md` rewrites its `aliases:` block; any user-added body content below the closing `---` survives.
7. Submit a note mentioning a brand-new name "Quincy Jones" once. `entity_suggestions` row exists with `occurrence_count=1` but does NOT surface in the panel (below threshold). After two more notes mention it, it surfaces.
8. Click Dismiss on a suggestion. Row disappears from panel, doesn't return on refresh, even on next ingest that re-mentions it.
9. Reprocess an existing synthesized note. New body has `[[wikilinks]]` selected by the synth model's judgment, not gated on any entity list.
10. No `IsProvisional` references remain in code. `is_provisional` column dropped.
