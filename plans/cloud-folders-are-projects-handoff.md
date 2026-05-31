# CLOUD-FOLDERS-ARE-PROJECTS — Drop the project entity, route into vault folders directly

**Goal:** eliminate the separate "Project" abstraction. Folders in the user's Obsidian vault become the only first-class routing target. The cloud derives candidate folders from synced state, the LLM router picks one (or returns null → Inbox), notes land in `<Folder>/<filename>.md`. The Projects sidebar panel, the `+ New Project` form, the `GET/POST /api/projects` endpoints, and `entities WHERE kind=project` rows all go away. Estimated **1–1.5 person-days** with AI-agent assistance.

## Why this exists

Two hierarchies that mean the same thing — Obsidian folders + cloud project entities — and the user has to maintain both. Today's gotchas:

- User creates `Cars/` in Obsidian → cloud doesn't know → routing can't suggest it.
- User accepts a routing into `Cars` → cloud creates the project entity → plugin then has to materialise the folder.
- User renames `Cars/` to `Vehicles/` → cloud's project entity has stale name → routing prompt is wrong.

Obsidian's native model is folder-driven. Users already think in folders to organise notes; adding a "Project" concept on top is a parallel hierarchy with no real user-facing payoff. The cloud-side routing pipeline is the only consumer of the project list — and the list it actually wants is *"where would a sensible note about this content live in the vault?"* which is **exactly the folder list**.

This ticket collapses the two hierarchies into one: **folders.**

## Scope

**In scope:**
- Routing fetches candidate folders from `notes.relative_path` parent paths (or from a per-ingest payload — see "Folder list source" below).
- `Features/Projects/*` deleted: endpoints, repository, DTOs.
- Plugin Projects panel deleted: sidebar section, `+ New Project` form, `listProjects`/`createProject` API calls.
- Plugin `BrowView` no longer queries projects.
- Routing prompt receives folder names (not entity names).
- Folder denylist: `_*`, `.*`, the configured `EntitySuggestions:StubsFolder` (default `Entities`), and `Inbox/` (Inbox is the *fallback*, not a routing target).
- Migration: soft-delete `entities WHERE kind = 'project'` rows. Don't drop the column (`kind` enum stays, it's still used for person/concept/place/other).
- Backend tests for the folder-derivation query and the routing path.
- Plugin: queue sidebar still shows project chips, but the chip data comes from folder names (no separate fetch).

**Out of scope:**
- Folder-rename detection in the plugin (Obsidian rename → cloud notification). For v1, renames in Obsidian are passively reflected via sync — when a note's `relative_path` changes, the cloud sees it next sync and the folder list updates implicitly. No explicit "folder renamed" event.
- Folder reordering / nesting preferences in the sidebar UI. Folders surface in alphabetical or creation order; sophistication later if needed.
- A new "rebuild folder cache" admin endpoint. Folder list is computed on-demand for each ingest.
- Backfilling existing project-entity routes onto folder paths. Old notes' `relative_path` is already set; this ticket doesn't move files.

## Folder list source

Two options, pick one:

**Option α — Derived from sync state (cloud-side query).**
At route-phase time, query:
```sql
SELECT DISTINCT split_part(relative_path, '/', 1) AS folder
FROM notes
WHERE deleted_at IS NULL
  AND relative_path IS NOT NULL
  AND split_part(relative_path, '/', 1) NOT IN ('Inbox', 'Entities')
  AND split_part(relative_path, '/', 1) NOT LIKE '\_%' ESCAPE '\'
  AND split_part(relative_path, '/', 1) NOT LIKE '.%'
ORDER BY folder
LIMIT 50;
```
Pros: zero plugin-side change for folder discovery, stateless. Cons: empty user-created folders (no notes yet) won't be candidates.

**Option β — Ingest payload carries folder list.**
Plugin enumerates its vault folder list at submit time, sends as `folder_hints: ["Cars", "Memory", "Marathon Training", ...]` in the ingest init payload. Cloud router uses it directly. Pros: preemptive folders work; user can create `Marathon Training/` empty and have routing target it. Cons: small payload bloat per ingest, plugin has to enumerate folders.

**Recommendation: ship α now; layer β later if observed.** Most users won't create empty preemptive folders — they create folders as notes accumulate. α handles the natural-growth case, requires zero plugin work, and is one SQL change in the cloud. β is an additive enhancement that doesn't conflict with α.

## Concrete files

### Backend

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/RoutingHandler.cs` — EDIT

Today probably loads `entities WHERE kind = 'project'`. Replace with a folder query (Option α). Resulting list feeds `PromptTemplates.RouteV1` in place of the projects list.

If the routing-prompt format string `{0}` expects "id | name" pairs, switch to plain folder names (no UUIDs — there are no entity ids anymore). Update `PromptTemplates.RouteV1` formatting accordingly.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/Prompts/PromptTemplates.cs` — EDIT

```csharp
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
```

Drop the `project_entity_id` field; return a folder name string instead. Update `RouteV1Format`, the response parser, and the routing-decision DTO.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/RoutingHandler.cs` — second edit

The decision now sets `note.RelativePath = $"{folder}/{filename}.md"` directly. No entity lookup, no UUID. If `folder` is null → `note.RelativePath = $"Inbox/{filename}.md"`.

#### Delete `src/ThanyMarcus.Cloud.Api/Features/Projects/` entirely.

Files: `ProjectsEndpoints.cs`, any DTOs in there, any repository. Also unregister `MapProjectsEndpoints` (or whatever it's called) in `Program.cs`.

#### `src/ThanyMarcus.Cloud.Api/Features/Entities/` — minor

If there's code that constructs/lists project entities, drop those paths. The entity kind enum stays (`person`, `organization`, `place`, `concept`, `other`). Drop `project` from any place it's still a valid kind for new rows. Existing rows can be soft-deleted by the migration below.

#### Migration `0NNN_drop_project_entities.sql`

```sql
-- Projects are now vault folders, not entities. Soft-delete existing project rows.
UPDATE entities SET deleted_at = NOW() WHERE kind = 'project' AND deleted_at IS NULL;

-- Tighten the kind check constraint to exclude 'project' going forward.
ALTER TABLE entities DROP CONSTRAINT IF EXISTS ck_entities_kind;
ALTER TABLE entities ADD CONSTRAINT ck_entities_kind
    CHECK (kind IN ('person','organization','place','concept','other'));

-- Same for suggestions (per the entity-suggestions ticket).
ALTER TABLE entity_suggestions DROP CONSTRAINT IF EXISTS ck_entity_suggestions_kind;
ALTER TABLE entity_suggestions ADD CONSTRAINT ck_entity_suggestions_kind
    CHECK (kind IN ('person','organization','place','concept','other'));
```

`mentions` rows pointing at the now-deleted project entities are orphan-safe (FK is `ON DELETE SET NULL`). They become "mentions of an entity that no longer exists" — harmless, can be tombstoned in a later cleanup.

### Plugin

#### `plugin/thany-marcus/src/api.ts` — REMOVE

```ts
listProjects, createProject, deleteProject     // gone
ProjectDto interface                            // gone
```

#### `plugin/thany-marcus/src/sidebar/` — REMOVE

The Projects panel component and its mount in `QueueSidebar`. The `+ New Project` form goes with it.

#### `plugin/thany-marcus/src/queue/QueueSidebar.ts` — EDIT

Drop `this.projects` field, `refreshProjects()` call in `onOpen`, the projects rendering section. The queue sidebar shows queue rows + entity suggestions only.

#### `plugin/thany-marcus/src/draft/BrowView.ts` — EDIT (if applicable)

If the BrowView surfaces project chips (it may not — verify), remove that block. The note's eventual folder is decided server-side, so the composer doesn't need to display projects pre-submit.

#### `plugin/thany-marcus/src/main.ts` — EDIT

Remove the project actions wiring (the `list:` and `create:` callbacks fed into the queue sidebar). The sidebar constructor signature simplifies.

## Tests

### Backend (`tests/ThanyMarcus.Cloud.Tests/Features/Processing/RoutingHandlerFoldersTests.cs` — NEW or rewrite)
- Empty notes table → folder list is empty → router gets `(none)` placeholder → returns null → note routes to Inbox.
- Notes scattered across `Cars/`, `Memory/`, `Inbox/`, `_attachments/`, `Entities/`, `.trash/` → folder list returns `["Cars", "Memory"]` (Inbox + denylisted folders excluded).
- Routing prompt receives folder names, no UUIDs.
- Returned routing decision is a folder name string (or null), not a UUID.
- `note.RelativePath` is set to `<folder>/<slug>.md` after successful routing.

### Plugin
- Delete `ProjectsPanel.test.ts` (component gone).
- Update `QueueSidebar.test.ts` to no longer assert project rendering.
- Verify `BrowView.test.ts` still passes after removing project chip assertions (if any).

## Migration / deployment notes

- Apply the migration to soft-delete project entities. No data destroyed; rows have `deleted_at` set.
- Cloud-api image needs rebuild + push (smaller binary after Projects feature deletion).
- Plugin needs rebuild + copy to demo vault.
- **Cache-invalidation:** the route-phase prompt content changes (folder names ≠ entity-id-and-names), so synth caches keyed on `mentionsHash`/`routingHash` may need to be invalidated. Bump `RouteV1` version constant if one exists; otherwise clear the synth cache table on first deploy.
- Existing notes' `relative_path` is unchanged. The migration only affects *future* routing decisions.
- Hard-deleting the orphan `mentions` rows is a follow-up cleanup; this ticket leaves them in place.

## Risks / open questions

- **Folder discovery latency.** The folder query runs at route-phase time per note. With `LIMIT 50` and a B-tree on `notes.relative_path`, the query is sub-10ms. No index work needed.
- **Routing prompt collisions.** Folder name "Cars" vs entity name "Cars" — the LLM gets the same surface string but no UUID anymore. Routing decision is now a string match against the candidate list, so the parser must verify the model returned an EXACT folder name from the list (or null). Reject hallucinated folder names by re-routing to Inbox.
- **Renames in Obsidian.** Today, if a user renames `Cars/` to `Vehicles/` in Obsidian, the plugin's sync layer eventually pushes the updated `relative_path` to the cloud per note. The folder query then returns `Vehicles` as a candidate. The transition window (some notes still at `Cars/`, others at `Vehicles/`) produces a router that briefly sees both — harmless; LLM picks the better match for the current note's content.
- **What if the user genuinely wants the cloud to track a separate "project" semantic** — e.g., for cross-vault dashboards in a hypothetical future shared space? This ticket assumes single-vault. Restoring a project abstraction later is reversible; we're not painting into a corner.
- **Entity suggestions for a person who *coincidentally* has the same name as a folder.** Suggestion has `kind = person`, folder has the same string. They live in different conceptual spaces (`Entities/Michael Jackson.md` vs `Michael Jackson/` folder for the user's MJ research). User can pick. No mechanism stops both from existing.

## Done = ?

1. Plugin reloads with no Projects panel anywhere in the UI.
2. Submit a note containing "I'm working on the Cars project plan." With no `Cars/` folder yet → routes to `Inbox/`.
3. Drag any note in Obsidian into a new folder `Cars/`. Submit another note about Cars → cloud's folder query now sees `Cars` → routing picks it → note lands at `Cars/<slug>.md`.
4. Rename the folder in Obsidian to `Vehicles/`. Plugin syncs the path change. Submit a third note about cars → routes to `Vehicles/`.
5. Create folder `_drafts/` in Obsidian. Submit a note → never routes there (denylisted), routes to `Inbox/` or a non-denylisted match.
6. `entities WHERE kind = 'project'` returns zero live rows; check constraint rejects `project` as a kind for any new insert.
7. `GET /api/projects` returns 404 (endpoint deleted). No `listProjects` calls in plugin code.
