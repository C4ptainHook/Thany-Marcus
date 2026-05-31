# CLOUD-QUEUE-REHYDRATE — Survive Obsidian reload mid-flight

**Goal:** the plugin's queue sidebar must repopulate from cloud state on every plugin/view re-open, not only from new SSE events. Closes the demo failure mode where reloading Obsidian during an in-flight ingest erases the queue UI while the note continues processing invisibly in the cloud. Estimated **0.5 person-day** with AI-agent assistance.

## Why this exists

Today the plugin's queue store is fed only by SSE events:

- `POST /api/ingest/init` → SSE: `queued`, `extracting_attachments`, `composing`, ...
- The plugin's `QueueStore` appends rows as events arrive and updates row status in-place.
- On Obsidian reload, the in-memory store is wiped. SSE reconnects, but only future events stream — past events are not replayed.

Net effect: if you submit a note, reload Obsidian, the queue panel is empty even though the saga is still progressing on the droplet. When the note finally hits `ready`, the vault writer drops the file in but the queue UI never showed the transition. Worse, **failed** notes that need a Reprocess click become invisible — the user has no UI surface to retry them after a reload.

Symmetric fixes already exist for Projects (`onOpen → refreshProjects()`) and Entity Suggestions (`Panel.start()` polls on mount). The queue is the only stateful panel still SSE-only.

## Scope

**In scope:**
- New backend endpoint `GET /api/ingest/jobs?status=active` returning all non-terminal saga jobs for this cloud, with the fields the queue UI renders.
- Optional `?include=recent` query flag to also return the most-recent N terminal jobs (default 10) so the "Recent notes" list shows post-reload.
- Plugin `Api.listActiveJobs()` method.
- `QueueSidebarView.onOpen()` calls `listActiveJobs()` and hydrates the local store before subscribing to SSE.
- Hydration is idempotent: if SSE has already delivered a row for the same noteId before hydration completes, the store de-dupes by noteId and uses the SSE row (it's strictly more recent).
- Backend test for the new endpoint (active filter, recent filter, auth gate).
- Plugin test: `QueueSidebarView` populates from `listActiveJobs()` before SSE delivers events.

**Out of scope:**
- SSE event replay (server-side event store retention). This ticket fetches the *current state*, not the *event history*. If the user reloaded during phase `synthesizing` and the saga has since reached `ready`, the hydration shows the current `ready` row — they don't see the intermediate transitions. That's fine.
- Cross-device queue sync. Single-vault, single-plugin assumption stays.
- Configurable "recent N" — hardcoded N=10 in v1; tune if observed.
- Pagination. The active set is bounded by the saga's `MaxConcurrentJobs` setting (currently 3) plus very recent terminals — well under any practical pagination threshold.

## Backend

### `src/ThanyMarcus.Cloud.Api/Features/Ingest/ListJobsEndpoint.cs` — NEW

```csharp
public static partial class ListJobsEndpoint
{
    public static void MapListJobsEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/ingest/jobs", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("GetIngestJobs")
            .Produces<ListJobsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    public sealed record ListJobsItem(
        Guid   NoteId,
        string Title,                // first H1 of body_output, or first 60 chars of body_input, or "(untitled)"
        string Status,               // ingest_jobs.status
        int    AttemptCount,
        string? Error,               // ingest_jobs.last_error
        string? VaultPath,           // notes.relative_path (null until composed)
        IReadOnlyList<ExtractionFailureItem> ExtractionFailures);

    public sealed record ExtractionFailureItem(
        string Kind,                 // image | voice | url | file
        string Reason);

    public sealed record ListJobsResponse(
        IReadOnlyList<ListJobsItem> Active,
        IReadOnlyList<ListJobsItem> Recent);   // empty unless ?include=recent

    private static async Task<IResult> HandleAsync(
        [FromQuery] string? status,
        [FromQuery] string? include,
        CloudDbContext db,
        CancellationToken ct)
    {
        // status=active is the only supported value in v1; reject anything else.
        // include=recent triggers the terminal-tail query.
        // Active = ingest_jobs WHERE status NOT IN (succeeded, failed_extraction, failed_routing,
        //                                         failed_synthesis, failed_embedding, cancelled)
        //         ORDER BY created_at DESC
        // Recent = ingest_jobs WHERE status IN (succeeded, failed_*, cancelled)
        //          ORDER BY updated_at DESC LIMIT 10
        // For each: JOIN notes, project Title from body_output H1 (regex) or body_input prefix.
        // ExtractionFailures: JOIN attachments WHERE extraction_status = 'failed'.
        //   (extracted_minimal does NOT count — per the URL-never-fails ticket.)
    }
}
```

Wire in `Program.cs` next to the other ingest endpoints.

### Title extraction helper

Reuse `SynthesizingHandler.ExtractTitleSlug` (or its underlying regex) — that's where the canonical "find an H1" logic already lives.

### Reuse considerations

The existing SSE event payload shape (`QueueRowDto` or similar — find via plugin's SSE handler) should be mirrored exactly by `ListJobsItem`, so the plugin's reducer can ingest both without branching.

## Plugin

### `plugin/thany-marcus/src/api.ts` — ADD

```ts
interface IngestJob {
    noteId: string;
    title: string;
    status: string;              // matches SSE phase strings
    attemptCount: number;
    error: string | null;
    vaultPath: string | null;
    extractionFailures: { kind: string; reason: string }[];
}

interface ListJobsResponse {
    active: IngestJob[];
    recent: IngestJob[];
}

async listActiveJobs(includeRecent = true, signal?: AbortSignal): Promise<ListJobsResponse>
```

POST to `GET /api/ingest/jobs?status=active${includeRecent ? "&include=recent" : ""}`.

### `plugin/thany-marcus/src/queue/QueueSidebarView.ts` — EDIT `onOpen`

```ts
async onOpen(): Promise<void> {
    this.suggestionsHost = document.createElement("div");
    this.suggestionsPanel = new EntitySuggestionsPanel(/* ... */);
    this.suggestionsPanel.start();

    this.unsubscribe = this.store.subscribe(() => this.render());

    // NEW: hydrate from cloud before relying on SSE
    try {
        const { active, recent } = await this.api.listActiveJobs(true);
        this.store.hydrate([...active, ...recent]);  // store.hydrate de-dupes by noteId
    } catch (e) {
        console.error("[Thany] listActiveJobs failed", e);
        // Soft fail — SSE will eventually populate; the UI just stays blank longer
    }

    this.render();
    void this.refreshProjects();
}
```

### `plugin/thany-marcus/src/queue/QueueStore.ts` — ADD `hydrate`

```ts
hydrate(jobs: IngestJob[]): void {
    for (const job of jobs) {
        if (this.byId.has(job.noteId)) continue;   // SSE wins if it landed first
        const entry = jobToStoreEntry(job);
        this.byId.set(job.noteId, entry);
    }
    this.notify();
}
```

The `jobToStoreEntry` mapper already exists for SSE; reuse it.

## Tests

### Backend (`tests/ThanyMarcus.Cloud.Tests/Features/Ingest/ListJobsEndpointTests.cs` — NEW)
- Returns 401 without plugin auth.
- Active filter omits terminal-status jobs.
- Recent filter returns top-10 by `updated_at` DESC.
- Title comes from H1 when `body_output` is present, body prefix otherwise, `"(untitled)"` when both missing.
- ExtractionFailures excludes `extracted_minimal` (URL-attachment minimal outcomes do NOT surface as failures).
- Empty database returns `{ active: [], recent: [] }`.

### Plugin (`plugin/thany-marcus/tests/queue/QueueSidebar.hydrate.test.ts` — NEW)
- `onOpen` calls `api.listActiveJobs(true)` exactly once.
- Hydrated rows render before any SSE event arrives.
- If SSE delivers an event for the same noteId mid-hydration, the SSE row wins (de-dup by noteId).
- `listActiveJobs` failure renders empty queue but does not throw.

## Done = ?

1. Submit a note. Wait until phase `synthesizing`. Close Obsidian.
2. Reopen Obsidian. Queue sidebar shows the in-flight row with phase `synthesizing` and the correct title.
3. SSE reconnects; on the next phase transition, the row updates to `embedding` then `ready`.
4. Submit a note, wait for it to fail (e.g. provoke a synth error). Close + reopen Obsidian. Failed row appears in the queue with Reprocess button visible.
5. With 0 active jobs but 3 historic notes, reopen Obsidian. "Recent notes" list shows those 3 (no in-flight rows).
6. With a totally empty cloud, reopen Obsidian. Queue shows the "No recent submissions" empty state, no errors logged.

## Risks / open questions

- **Race between hydrate and SSE.** If SSE has already opened and delivered a `queued` event for noteId X before `listActiveJobs` resolves, the hydrate path must not overwrite the SSE row. The `if (this.byId.has(job.noteId)) continue;` guard handles this — verify in the test.
- **Title computation cost.** For each active job, we read `notes.body_output` to extract H1. With `MaxConcurrentJobs=3` + 10 recent, that's 13 row reads — trivial. No index work needed.
- **Sensitive content in titles.** Body prefixes might include private content. The endpoint is gated by `RequirePluginAuthFilter` (same as everything else), so this is no new exposure.
- **SSE replay scope creep.** Out-of-scope here, but worth noting: a future ticket could add a `?since=ts` parameter to replay the event log for richer in-flight UI. Don't conflate with this ticket — current scope is "show the current state," not "show the journey."
