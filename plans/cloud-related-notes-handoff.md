# CLOUD-RELATED-NOTES — Capture-time related-notes panel

**Goal:** as the user composes a new note in the plugin, surface 3–5 thematically related notes from their vault in a sidebar panel, ranked by vector similarity over the embeddings the saga already populates. Closes the *capture → re-encounter* loop; turns the existing capture pipeline into a working second-brain surface. Estimated **1.25 person-days** with AI-agent assistance.

## Why this exists

The ingest saga populates `notes.embedding` (256-dim granite, see `EmbeddingHandler.cs`) on every `ready` note, but **nothing queries it**. The column has been live since the embedding phase landed and has accumulated data for every synthesized note. Today that data is dead weight — it costs disk + index bytes and produces zero user-visible value.

The capture surface (composer in the Obsidian plugin) is where the user has the highest signal about what they're thinking about: they're actively typing a draft. That is the moment when *"here are three thoughts from your past notes that connect to this"* converts an empty-vault feeling into a second-brain feeling. Today that moment is unused — the composer is a sealed box; no past content surfaces while the user types.

Smart Connections (Obsidian plugin) ships exactly this UX over the user's vault, using OpenAI embeddings. Mem and Reflect both surface related notes during composition. Thany-marcus already has the embeddings + entity graph + per-project structure that these tools approximate; what's missing is the read path and the UI.

This ticket lands the minimum loop: ingest → embed → retrieve → surface at next capture.

## Scope

**In scope:**
- New `POST /api/notes/related` endpoint on cloud-api, taking either a draft body or a noteId, returning top-k similar notes.
- `NoteVectorQueries.NearestAsync` raw-SQL helper mirroring the existing `EntityVectorQueries.NearestAsync`.
- pgvector HNSW index on `notes.embedding` for sub-100ms kNN as the vault grows past ~5k notes.
- In-memory `QueryEmbeddingCache` keyed by `hash(body)` with 60-90s TTL, to avoid re-embedding the same draft on every keystroke debounce.
- New plugin sidebar panel `RelatedNotesPanel` in the composer view, debounced on input, shows 3–5 results with title + 1-line snippet + click-to-open.
- Time-decay filter: exclude notes created within the last hour from results.
- Distance threshold: hide results with cosine distance above a configurable ceiling (start at 0.7, tune via observation).
- Backend tests for the kNN query and endpoint; plugin tests for debounce + restore behaviour.

**Out of scope:**
- Hybrid (FTS + kNN + graph) retrieval — kNN alone for v1; layer additional signals if recall feels weak.
- Cross-encoder re-ranking — adds complexity and another ONNX model; defer until kNN quality is observed in real use.
- Click-to-insert-wikilink — surfacing a note is enough for v1; converting surfacing into an explicit wikilink in the draft is a follow-up.
- Q&A over notes ("ask my vault") — RAG flow; separate ticket, depends on this.
- Daily/weekly digest — push-surfacing flow; separate ticket, also depends on retrieval.
- Search UI / search box — different use case (explicit query) where FTS-first probably wins; this ticket is capture-time-only.

## Why kNN-only for v1

Three retrieval signals are theoretically available: FTS (Postgres `tsvector`), vector kNN (already-populated embeddings), wikilink graph (entities + bodies). For capture-time surfacing specifically, kNN alone is the right v1 because:

1. The user is producing free-form thoughts, not formulating a query. Paraphrase matches ("audience feedback loop" → "customer input channel") are exactly what kNN is good at and exactly what FTS misses by design.
2. FTS's strengths (proper-noun recall, predictability) matter for an explicit search box where the user has expectations about which words they typed. They don't earn anything in a passive sidebar the user glances at.
3. The wikilink graph signal only fires after the draft contains `[[X]]`, which is rare mid-composition. It's a strong precision signal but a weak recall signal at capture time.

Layering FTS or graph signals later is cheap (RRF fusion is ~30 lines). Doing it now adds complexity v1 doesn't need.

## State machine semantics

None — the endpoint is stateless. Request in, top-k out. The vector cache is a process-local optimisation, not a state machine.

## Concrete files

### Backend

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/NoteVectorQueries.cs` — NEW

Mirror `EntityVectorQueries.NearestAsync`. Raw SQL because Pgvector.EntityFrameworkCore 0.x does not surface `<=>` into LINQ:

```csharp
public static class NoteVectorQueries
{
    public static async Task<List<RelatedNote>> NearestAsync(
        CloudDbContext db,
        float[] query,
        int k,
        Guid? excludeNoteId,
        Instant excludeCreatedAfter,   // for time-decay: only return notes created BEFORE this
        CancellationToken ct)
    {
        // SELECT id, relative_path, body_output, embedding <=> @emb AS distance
        // FROM notes
        // WHERE deleted_at IS NULL
        //   AND status = 'ready'
        //   AND created_at < @excludeAfter
        //   AND (@excludeId IS NULL OR id <> @excludeId)
        // ORDER BY embedding <=> @emb
        // LIMIT @k
    }
}

public sealed record RelatedNote(
    Guid Id,
    string RelativePath,
    string BodySnippet,    // first ~140 chars of body_output, post-frontmatter
    double Distance);
```

Snippet extraction: read first H1 + first paragraph, cap at 140 chars. Strip frontmatter (`---` block at start of body_output).

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/RelatedNotesEndpoint.cs` — NEW

```csharp
public static partial class RelatedNotesEndpoint
{
    public static void MapRelatedNotesEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/notes/related", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("PostNotesRelated")
            .Produces<RelatedNotesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    public sealed record RelatedNotesRequest(
        string? Body,           // free text from the draft composer
        Guid? NoteId,           // OR an existing note's id (uses its stored embedding)
        int? K);                // default 5, max 20

    public sealed record RelatedNotesItem(
        Guid Id, string RelativePath, string Title, string Snippet, double Distance);

    public sealed record RelatedNotesResponse(IReadOnlyList<RelatedNotesItem> Items);

    private static async Task<IResult> HandleAsync(
        RelatedNotesRequest req, CloudDbContext db, IEmbeddingClient embeddings,
        QueryEmbeddingCache cache, IClock clock, CancellationToken ct)
    {
        // 1. Validate: exactly one of Body | NoteId. K in [1, 20], default 5.
        // 2. Resolve query vector:
        //    - NoteId path: read embedding column, 404 if not found or no embedding.
        //    - Body path: cache.GetOrAdd(hash(body), () => embeddings.EmbedAsync(body)).
        //                 Skip cache and 400 if body length < 30 chars.
        // 3. Compute excludeCreatedAfter = clock.GetCurrentInstant() - Duration.FromHours(1).
        // 4. NoteVectorQueries.NearestAsync(...).
        // 5. Filter by distance threshold (config: IngestSaga:Retrieval:MaxDistance, default 0.7).
        // 6. Project to RelatedNotesItem, return 200.
    }
}
```

Title extraction: parse the H1 from `body_output` (the same `H1Title` regex already used in `SynthesizingHandler.ComputeRelativePath`).

Wire in `Program.cs` next to the other note endpoints.

#### `src/ThanyMarcus.Cloud.Api/Infrastructure/Embeddings/QueryEmbeddingCache.cs` — NEW

Process-local cache. Use `MemoryCache` with `AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90)` and `SizeLimit` (cap at ~500 entries). Key = SHA-256 of the body string. Value = `float[]`. Singleton registration in DI.

The point of the cache: as the user types, the debounced query fires every 500-800ms. Many of those fires will be on the *same* body (the user paused briefly then resumed). Without cache, each fire re-runs the granite ONNX embed, which is ~50-100ms but burns CPU on the droplet. With cache, repeated queries on the same body hit the dict and return immediately.

#### Postgres migration `0NNN_notes_embedding_hnsw_index.sql`

```sql
-- HNSW index on notes.embedding for sub-100ms kNN past ~5k notes.
-- ivfflat is the alternative; HNSW has higher build cost but better query latency.
-- The 256-dim granite vectors fit either; HNSW is the safer default for read-heavy.
CREATE INDEX IF NOT EXISTS notes_embedding_hnsw
ON notes USING hnsw (embedding vector_cosine_ops)
WHERE deleted_at IS NULL;
```

Partial index on `WHERE deleted_at IS NULL` because deleted notes are never returned anyway.

#### Configuration in `appsettings.json`

```json
"IngestSaga": {
  "Retrieval": {
    "MaxDistance": 0.7,
    "DefaultK": 5,
    "ExcludeRecentHours": 1,
    "MinBodyChars": 30,
    "QueryCacheTtlSeconds": 90,
    "QueryCacheMaxEntries": 500
  }
}
```

### Plugin

#### `plugin/thany-marcus/src/api.ts` — add method

```ts
interface RelatedNotesItem {
    id: string;
    relativePath: string;
    title: string;
    snippet: string;
    distance: number;
}

async relatedNotes(input: { body?: string; noteId?: string; k?: number },
                   signal?: AbortSignal): Promise<RelatedNotesItem[]>
```

POST to `/api/notes/related`. Pass the AbortSignal so superseded queries (user kept typing) can be cancelled cleanly — the previous fetch's abort prevents stale results from clobbering newer ones.

#### `plugin/thany-marcus/src/composer/RelatedNotesPanel.ts` — NEW

```ts
class RelatedNotesPanel {
    private debounceMs = 600;
    private minChars = 30;
    private abortCtl?: AbortController;
    private debounceTimer?: number;

    constructor(private readonly api: Api, private readonly container: HTMLElement) {}

    onBodyChange(body: string): void {
        // 1. Cancel pending debounce timer + any in-flight fetch.
        // 2. If body.length < minChars: render empty state, return.
        // 3. Start new debounce timer.
        //    On fire: abortCtl = new AbortController().
        //    api.relatedNotes({ body, k: 5 }, abortCtl.signal).then(render).
        // 4. On AbortError: ignore (superseded by next keystroke).
    }

    private render(items: RelatedNotesItem[]): void {
        // 3-5 rows: title (clickable, opens via app.workspace.openLinkText),
        //           snippet (1 line, truncated with ellipsis),
        //           small distance badge (optional, dev-only).
        // Empty state: "No related thoughts yet" or hide section entirely.
    }
}
```

Click handler: `app.workspace.openLinkText(item.relativePath, '', false)` opens the note in the active pane.

#### `plugin/thany-marcus/src/composer/Composer.ts` — wire panel

In `onOpen()`, instantiate `RelatedNotesPanel` and place it in a fixed sidebar slot below the existing draft controls. In the body-edit `oninput` listener, call `panel.onBodyChange(currentBody)`.

#### `plugin/thany-marcus/styles.css` — styling

```css
.tm-related {
    margin-top: 1em;
    border-top: 1px solid var(--background-modifier-border);
    padding-top: 0.5em;
}
.tm-related__header { font-size: 0.85em; color: var(--text-muted); }
.tm-related__row { padding: 0.4em 0; cursor: pointer; }
.tm-related__row:hover { background: var(--background-modifier-hover); }
.tm-related__title { font-weight: 600; font-size: 0.9em; }
.tm-related__snippet { font-size: 0.8em; color: var(--text-muted); }
```

## Tests

Backend:
- `NoteVectorQueriesTests.NearestAsync_returns_in_distance_order` — seed 5 notes with handcrafted vectors, assert ordering.
- `NoteVectorQueriesTests.NearestAsync_excludes_deleted` — soft-deleted notes never returned.
- `NoteVectorQueriesTests.NearestAsync_excludes_recent` — notes inside the time-decay window excluded.
- `NoteVectorQueriesTests.NearestAsync_excludes_self` — when `excludeNoteId` set, that row never appears.
- `RelatedNotesEndpointTests.Body_path_embeds_and_returns_results`.
- `RelatedNotesEndpointTests.NoteId_path_uses_stored_embedding_no_embed_call` — assert the embed client is not invoked.
- `RelatedNotesEndpointTests.NoteId_404_when_note_or_embedding_missing`.
- `RelatedNotesEndpointTests.Distance_above_threshold_filtered_out`.
- `RelatedNotesEndpointTests.Body_too_short_returns_400`.
- `QueryEmbeddingCacheTests.Hit_skips_embed_client` and `Miss_invokes_embed_client_once`.

Plugin:
- `RelatedNotesPanel.debounce_collapses_rapid_keystrokes` — 5 rapid `onBodyChange` calls within 600ms → 1 fetch.
- `RelatedNotesPanel.abort_supersedes_in_flight` — new query while previous fetch in-flight aborts the previous.
- `RelatedNotesPanel.short_body_renders_empty_state_no_fetch`.
- `RelatedNotesPanel.click_opens_via_workspace`.

## Risks and edge cases

- **Empty vault → empty panel.** If the user has no `ready` notes, every query returns `[]`. Render an empty state instead of nothing; ideally a one-time hint like "related thoughts will appear here as your vault grows."
- **kNN returns near-duplicates of one source note.** If the user wrote 5 atomic notes about the same project, top-5 are all near each other. Worth adding MMR (Maximal Marginal Relevance) post-processing in v1.5 if user feedback flags it; not v1.
- **Granite 256d quality bound.** Some queries will surface unrelated notes. The distance threshold mitigates the worst cases; the time-decay filter mitigates re-surfacing recent-and-obvious matches. If quality is consistently poor, the answer is a better embedding model (mxbai-embed-large is the obvious next step at ~670MB), not architecture changes.
- **Cache thrash on long sessions.** 500-entry cap + 90s TTL keeps memory bounded. If a user types continuously for hours and hits the cap, oldest entries evict — acceptable.
- **Abort race.** AbortController on the client cancels the HTTP request but the server still computes the embedding + runs the query. Cost is bounded (each cancelled request wastes <150ms server-side); acceptable for v1, can add server-side cancellation token propagation later.
- **Note that was queried gets edited/deleted between query and click.** Click handler should handle "note not found" gracefully (Obsidian will create a stub; surface a non-blocking toast).

## Effort breakdown

- Backend kNN query + endpoint + cache + index: 3h
- Backend tests: 2h
- Plugin API method + panel + composer wiring: 3h
- Plugin tests: 2h
- Polish (styling, empty states, distance threshold tuning against a real vault): 2h

Total: **~1.25 person-days**. MVP cut (no cache, no time-decay, no abort handling — just naive debounce): **~0.5 person-days**, but the experience will jitter and burn CPU.

## Decision points to confirm before starting

1. **Where in the composer UI does the panel live?** Right sidebar (matches QueueSidebar), bottom of composer (below the draft body), or floating above? Affects component placement, not core logic.
2. **Show the distance score?** Dev-only debug badge, or hide entirely? Default: hide; expose via a dev flag.
3. **Click action — open in vault, or insert wikilink, or both?** Default: open in vault. Wikilink-insert is a follow-up.
4. **Cache TTL — 90s or shorter?** Longer TTL = fewer embed calls but staler if the user is iterating fast. 90s is a guess; tune with usage data.
5. **Time-decay window — 1 hour or different?** Goal is to exclude "I just wrote that, not interesting"; 1h is a guess. May need to grow to 6h or shrink to 15m based on observation.

Owner picks at kickoff; this ticket does not block on the answers.

## Follow-on tickets this unlocks

- **CLOUD-RELATED-NOTES-HYBRID** — layer FTS + wikilink-graph signals with RRF fusion if kNN-only recall feels weak in practice.
- **CLOUD-RELATED-NOTES-MMR** — MMR diversification of top-k to avoid near-duplicate clusters.
- **CLOUD-DIGEST** — weekly/daily push-surfacing using the same kNN + entity-graph machinery, delivered out-of-band.
- **CLOUD-COMPOSE-FROM** — "compose blog draft from these notes" action; takes selected top-k from this panel and runs synthesis over them. The stage-3 action layer.
- **CLOUD-VAULT-QA** — RAG over notes for free-form Q&A. Uses the same kNN endpoint, adds an LLM call on top of the retrieved candidates.
